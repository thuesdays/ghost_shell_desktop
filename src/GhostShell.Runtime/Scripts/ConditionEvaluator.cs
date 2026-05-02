// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Models;
using GhostShell.Core.Services;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Evaluates <see cref="ScriptCondition"/> trees against the live
/// browser session + script <see cref="RunContext"/>.
///
/// Catalog of kinds (legacy parity, ~15 most-used):
///   • <c>true</c>  / <c>false</c>           — constants
///   • <c>and</c> / <c>or</c> / <c>not</c>   — compound (read Children)
///   • <c>var_equals</c>      — Params: name, value
///   • <c>var_exists</c>      — Params: name
///   • <c>var_matches</c>     — Params: name, pattern (regex)
///   • <c>has_ads</c>         — context.Ads.Count > 0
///   • <c>ads_count_gte</c>   — Params: n
///   • <c>url_contains</c>    — Params: needle
///   • <c>url_matches</c>     — Params: pattern (regex)
///   • <c>title_contains</c>  — Params: needle
///   • <c>selector_present</c>— Params: selector  (querySelector returns truthy)
///   • <c>selector_visible</c>— Params: selector  (offsetParent !== null)
///   • <c>random</c>          — Params: probability  (0–1 uniform)
///   • <c>captcha_visible</c> — heuristic DOM scan for typical captcha selectors
///   • <c>own_domain</c>      — Params: href; true iff host equals page host
/// </summary>
public sealed class ConditionEvaluator
{
    public async Task<bool> EvaluateAsync(
        ScriptCondition? cond, IBrowserSession session, RunContext ctx,
        CancellationToken ct = default)
    {
        if (cond is null) return true;
        var kind = (cond.Kind ?? "").ToLowerInvariant();
        switch (kind)
        {
            case "true":  return true;
            case "false": return false;

            case "and":
                foreach (var c in cond.Children)
                    if (!await EvaluateAsync(c, session, ctx, ct)) return false;
                return true;
            case "or":
                foreach (var c in cond.Children)
                    if (await EvaluateAsync(c, session, ctx, ct)) return true;
                return false;
            case "not":
                if (cond.Children.Count == 0) return true;
                return !await EvaluateAsync(cond.Children[0], session, ctx, ct);

            case "var_equals":
            {
                var name = ParamString(cond.Params, "name") ?? "";
                var val  = ParamString(cond.Params, "value") ?? "";
                return ctx.Vars.TryGetValue(name, out var v)
                       && string.Equals(v, val, StringComparison.Ordinal);
            }
            case "var_exists":
            {
                var name = ParamString(cond.Params, "name") ?? "";
                return ctx.Vars.ContainsKey(name);
            }
            case "var_matches":
            {
                var name = ParamString(cond.Params, "name") ?? "";
                var pat  = ParamString(cond.Params, "pattern") ?? "";
                if (!ctx.Vars.TryGetValue(name, out var v)) return false;
                // Phase 21 audit fix: defend against ReDoS (catastrophic
                // backtracking on a malicious pattern + non-matching
                // input). 200ms is generous for any reasonable regex.
                try
                {
                    return System.Text.RegularExpressions.Regex.IsMatch(
                        v, pat,
                        System.Text.RegularExpressions.RegexOptions.None,
                        TimeSpan.FromMilliseconds(200));
                }
                catch { return false; }
            }

            case "has_ads":      return ctx.Ads.Count > 0;
            case "ads_count_gte":
            {
                var n = ParamInt(cond.Params, "n", 1);
                return ctx.Ads.Count >= n;
            }

            case "url_contains":
            {
                var needle = ParamString(cond.Params, "needle") ?? "";
                var url = await GetUrlAsync(session, ct);
                return url.Contains(needle, StringComparison.OrdinalIgnoreCase);
            }
            case "url_matches":
            {
                var pat = ParamString(cond.Params, "pattern") ?? "";
                var url = await GetUrlAsync(session, ct);
                // Phase 21 audit fix: regex timeout (ReDoS guard).
                try
                {
                    return System.Text.RegularExpressions.Regex.IsMatch(
                        url, pat,
                        System.Text.RegularExpressions.RegexOptions.None,
                        TimeSpan.FromMilliseconds(200));
                }
                catch { return false; }
            }

            case "title_contains":
            {
                var needle = ParamString(cond.Params, "needle") ?? "";
                var title  = await session.GetTitleAsync(ct) ?? "";
                return title.Contains(needle, StringComparison.OrdinalIgnoreCase);
            }

            case "selector_present":
            case "selector_visible":
            {
                var sel = ParamString(cond.Params, "selector") ?? "";
                if (string.IsNullOrEmpty(sel)) return false;
                var visibleOnly = kind == "selector_visible";
                var js = $$"""
                    (function() {
                      var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
                      if (!el) return false;
                      if ({{(visibleOnly ? "true" : "false")}})
                        return el.offsetParent !== null;
                      return true;
                    })()
                """;
                var r = await session.ExecuteScriptAsync(js, null, ct);
                return r is true;
            }

            case "random":
            {
                var p = ParamDouble(cond.Params, "probability", 0.5);
                return Random.Shared.NextDouble() < p;
            }

            case "captcha_visible":
            {
                // Loose heuristic — typical captcha hosts inject one of
                // these into the DOM. False positives are acceptable
                // (the if-handler usually just routes around).
                const string Js = """
                    return !!(document.querySelector('iframe[src*="recaptcha"]')
                          || document.querySelector('iframe[src*="hcaptcha"]')
                          || document.querySelector('div.g-recaptcha')
                          || document.querySelector('div.h-captcha')
                          || document.querySelector('#cf-challenge-running')
                          || /verifying you are human|are you a robot/i.test(document.body && document.body.innerText || ''));
                """;
                var r = await session.ExecuteScriptAsync(Js, null, ct);
                return r is true;
            }

            case "own_domain":
            case "ad_is_mine":
            {
                // Two paths:
                //   • Legacy <c>own_domain</c> with explicit "href" param
                //     → compare to live page host.
                //   • New <c>ad_is_mine</c> reads <c>ctx.CurrentAdHref</c>
                //     and compares to <c>ctx.MyDomains</c>.
                if (kind == "ad_is_mine")
                {
                    var host = ExtractHost(ctx.CurrentAdHref);
                    return !string.IsNullOrEmpty(host)
                        && DomainMatches(host, ctx.MyDomains);
                }
                var href = ParamString(cond.Params, "href")
                    ?? ctx.CurrentAdHref;  // fall back to current ad
                if (string.IsNullOrEmpty(href)) return false;
                if (!Uri.TryCreate(href, UriKind.Absolute, out var hu)) return false;
                var pageUrl = await GetUrlAsync(session, ct);
                if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pu)) return false;
                return string.Equals(hu.Host, pu.Host, StringComparison.OrdinalIgnoreCase);
            }

            // Phase 17 — web-parity ad-aware conditions. All three
            // read ctx.CurrentAdHref (set by foreach_ad on each lap).
            case "ad_is_target":
            {
                var host = ExtractHost(ctx.CurrentAdHref);
                return !string.IsNullOrEmpty(host)
                    && DomainMatches(host, ctx.TargetDomains);
            }
            case "ad_is_external":
            {
                // External = not a profile-owned domain (includes
                // targets and unrelated competitors).
                var host = ExtractHost(ctx.CurrentAdHref);
                if (string.IsNullOrEmpty(host)) return false;
                return !DomainMatches(host, ctx.MyDomains);
            }
            case "ad_is_competitor":
            {
                // Competitor = neither mine nor a paid target. The
                // "interesting strangers" bucket.
                var host = ExtractHost(ctx.CurrentAdHref);
                if (string.IsNullOrEmpty(host)) return false;
                return !DomainMatches(host, ctx.MyDomains)
                    && !DomainMatches(host, ctx.TargetDomains);
            }

            default:
                return false; // unknown kind → treat as false; safer than crashing
        }
    }

    // ─── Domain helpers (mirrors ScriptRunner) ─────────────────────

    private static string ExtractHost(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    private static bool DomainMatches(string host, HashSet<string> set)
    {
        if (set.Count == 0) return false;
        if (set.Contains(host)) return true;
        foreach (var d in set)
        {
            var trimmed = d.StartsWith("www.") ? d[4..] : d;
            if (host.EndsWith("." + trimmed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<string> GetUrlAsync(IBrowserSession s, CancellationToken ct)
    {
        try
        {
            var r = await s.ExecuteScriptAsync("return location.href;", null, ct);
            return r as string ?? "";
        }
        catch { return ""; }
    }

    private static string? ParamString(IReadOnlyDictionary<string, object?> p, string key)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return null;
        return v switch
        {
            string s        => s,
            JsonElement el  => el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText(),
            _               => v.ToString(),
        };
    }

    private static int ParamInt(IReadOnlyDictionary<string, object?> p, string key, int def)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return def;
        return v switch
        {
            int i           => i,
            long l          => (int)l,
            double d        => (int)d,
            string s when int.TryParse(s, out var x) => x,
            JsonElement el when el.ValueKind == JsonValueKind.Number => el.GetInt32(),
            _ => def,
        };
    }

    private static double ParamDouble(IReadOnlyDictionary<string, object?> p, string key, double def)
    {
        if (!p.TryGetValue(key, out var v) || v is null) return def;
        return v switch
        {
            int i           => i,
            long l          => l,
            double d        => d,
            string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          out var x) => x,
            JsonElement el when el.ValueKind == JsonValueKind.Number => el.GetDouble(),
            _ => def,
        };
    }
}

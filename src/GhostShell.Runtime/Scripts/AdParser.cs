// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Services;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Parses ads out of the current page DOM and stamps them with
/// <c>data-gs-ad-id="N"</c> so a later click_ad can re-locate the
/// anchor even if the SERP re-renders.
///
/// v1 scope: SERP-style ad blocks (Google ads, organic-results that
/// look like ads). Banner/native ads via Intersection Observer come
/// in iter 4.1. Full 4-tier fallback chain (stamped → URL fragment
/// → JS scan → re-parse) is also iter 4.1; we ship the stamped path
/// here as the primary lookup.
/// </summary>
public static class AdParser
{
    /// <summary>
    /// Inject the parser into the live page. Returns parsed ad
    /// records — also stamps the DOM so click_ad has a back-pointer.
    /// </summary>
    public static async Task<List<AdRecord>> ParseAsync(
        IBrowserSession session, CancellationToken ct = default)
    {
        // Ad-detection JS — covers Google's data-text-ad attribute,
        // div[role="region"][aria-label*="Sponsored"], the
        // shopping carousel, and the "Sponsored" pill text.
        // Returns an array of {id, href, title, displayUrl}.
        //
        // Phase 71kk fix — STRIP existing data-gs-ad-id stamps at the
        // start of every parse, then re-stamp from scratch. Pre-fix
        // the stamper had `if (el.hasAttribute('data-gs-ad-id')) return;`
        // which skipped any element already stamped by a PRIOR parse.
        // Effect: search_query's parse stamped 4 ads → foreach_ad's
        // re-parse returned 0 (all 4 were already stamped from the
        // previous call) → "no ads on SERP, ending loop" → ZERO
        // click_ad invocations across every run, even when ads were
        // visible on the SERP. That's the literal cause of the user's
        // "ADS=0 across the entire history" report. Stripping stamps
        // first makes every parse return the full current ad set,
        // and click_ad's stamped-selector path keeps working because
        // we re-stamp with the same id scheme right after.
        const string Js = """
            return (function() {
              // Clear any stale stamps from prior parses on this DOM.
              // Cheap — querySelectorAll('[data-gs-ad-id]') is fast
              // and the loop only touches elements we previously
              // stamped (typically <10 per SERP).
              document.querySelectorAll('[data-gs-ad-id]').forEach(function(e){
                e.removeAttribute('data-gs-ad-id');
              });
              var out = [];
              var i = 0;
              function stamp(el) {
                var anchor = el.tagName === 'A' ? el : el.querySelector('a[href]');
                if (!anchor || !anchor.href) return;
                if (el.hasAttribute('data-gs-ad-id')) return;
                el.setAttribute('data-gs-ad-id', String(i));
                anchor.setAttribute('data-gs-ad-id', String(i));
                var title = (anchor.innerText || anchor.textContent || '').trim().slice(0, 200);
                var disp  = '';
                try {
                  var u = new URL(anchor.href);
                  disp = u.hostname;
                } catch (e) {}
                out.push({id: i, href: anchor.href, title: title, displayUrl: disp});
                i++;
              }
              // 1. Google's classic data-text-ad
              document.querySelectorAll('[data-text-ad]').forEach(stamp);
              // 2. Modern Google ad slot (top-of-SERP / inline)
              document.querySelectorAll('[data-rw][data-hveid]').forEach(stamp);
              // 3. "Sponsored" labeled regions
              document.querySelectorAll('div[aria-label*="Sponsored" i], div[aria-label*="Реклама" i]').forEach(stamp);
              // 4. Generic .ads / .ad blocks
              document.querySelectorAll('.ads-ad, .ad-block, [data-ad]').forEach(stamp);
              return out;
            })();
        """;

        try
        {
            var raw = await session.ExecuteScriptAsync(Js, null, ct);
            return MaterialiseAdList(raw);
        }
        catch
        {
            return new List<AdRecord>();
        }
    }

    /// <summary>
    /// Click one ad with a 4-tier fallback chain. The legacy Python
    /// project hits the same problem — Google's SERP re-renders ad
    /// blocks aggressively, so a stamp from 30s ago may already be
    /// gone. Each tier costs one round-trip:
    ///   1. <b>Stamped selector</b>: fastest path, the common case.
    ///   2. <b>URL-fragment match</b>: <c>a[href*="..."]</c> on the
    ///      ad's tracker URL (Google routes ads through
    ///      <c>googleadservices.com</c>; the tracker token is stable
    ///      across re-renders for the same ad).
    ///   3. <b>JS domain scan</b>: walks every <c>a[href]</c>, finds
    ///      the first whose host matches the ad's display host.
    ///   4. <b>Re-parse</b>: ParseAsync the page again, look for an
    ///      ad with the same href; if found, click that.
    /// Returns the tier that landed (1..4) or 0 if all failed.
    /// </summary>
    public static async Task<int> ClickAsync(
        IBrowserSession session, AdRecord ad, CancellationToken ct = default)
    {
        // Phase 14 perf optimisation: combine tiers 1+2+3 into one
        // JS round-trip. The success case (tier 1, the common path)
        // saves zero round-trips, but failure paths drop from 4 RTs
        // to 2 — meaningful when the SERP re-renders frequently. The
        // JS returns the tier number that succeeded (0 = none).
        var fragment = ExtractFragment(ad.Href);
        var displayHost = ad.DisplayUrl ?? "";
        var tier123Js = $$"""
            return (function() {
              function dispatchClick(anchor) {
                if (!anchor) return false;
                try {
                  var r = anchor.getBoundingClientRect();
                  var cx = r.left + r.width / 2;
                  var cy = r.top + r.height / 2;
                  ['mouseover','mousedown','mouseup','click'].forEach(function(n) {
                    anchor.dispatchEvent(new MouseEvent(n,
                      {bubbles: true, cancelable: true, clientX: cx, clientY: cy, button: 0}));
                  });
                  return true;
                } catch (e) {
                  try { anchor.click(); return true; } catch (e2) { return false; }
                }
              }
              // Tier 1 — stamped selector
              try {
                var el = document.querySelector('[data-gs-ad-id="{{ad.StampId}}"]');
                if (el) {
                  var anchor = el.tagName === 'A' ? el : el.querySelector('a[href]');
                  if (anchor && dispatchClick(anchor)) return 1;
                }
              } catch (e) {}
              // Tier 2 — URL fragment
              try {
                var frag = {{JsonSerializer.Serialize(fragment)}};
                if (frag) {
                  var anchors = document.querySelectorAll('a[href]');
                  for (var i = 0; i < anchors.length; i++) {
                    if (anchors[i].href && anchors[i].href.indexOf(frag) !== -1) {
                      if (dispatchClick(anchors[i])) return 2;
                    }
                  }
                }
              } catch (e) {}
              // Tier 3 — host scan
              try {
                var host = {{JsonSerializer.Serialize(displayHost)}}.toLowerCase();
                if (host) {
                  var anchors = document.querySelectorAll('a[href]');
                  for (var i = 0; i < anchors.length; i++) {
                    try {
                      var u = new URL(anchors[i].href);
                      if (u.hostname.toLowerCase() === host
                          || u.hostname.toLowerCase().endsWith('.' + host)) {
                        if (dispatchClick(anchors[i])) return 3;
                      }
                    } catch (e2) {}
                  }
                }
              } catch (e) {}
              return 0;
            })();
        """;

        var combined = await session.ExecuteScriptAsync(tier123Js, null, ct);
        var combinedTier = ToTier(combined);
        if (combinedTier > 0) return combinedTier;

        // Tier 4 — reparse + match (separate round-trip; the page may
        // have re-rendered since the prior parse, so this is a fresh
        // scan). Worst case we end up at 2 RTs total.
        var fresh = await ParseAsync(session, ct);
        var match = fresh.FirstOrDefault(a =>
            string.Equals(a.Href, ad.Href, StringComparison.OrdinalIgnoreCase));
        if (match is not null && await TryStampedClick(session, match.StampId, ct))
            return 4;

        throw new InvalidOperationException(
            $"Ad click failed (all 4 tiers): {ad.Href}");
    }

    private static int ToTier(object? raw)
    {
        return raw switch
        {
            int i        => i,
            long l       => (int)l,
            double d     => (int)d,
            string s     => int.TryParse(s, out var x) ? x : 0,
            System.Text.Json.JsonElement el when el.ValueKind == System.Text.Json.JsonValueKind.Number
                         => el.GetInt32(),
            _ => 0,
        };
    }

    private static async Task<bool> TryStampedClick(
        IBrowserSession session, int stampId, CancellationToken ct)
    {
        var js = $$"""
            return (function() {
              var sel = '[data-gs-ad-id="{{stampId}}"]';
              var el = document.querySelector(sel);
              if (!el) return false;
              var anchor = el.tagName === 'A' ? el : el.querySelector('a[href]');
              return DispatchMouseChain(anchor);
            })();
            function DispatchMouseChain(anchor) {
              if (!anchor) return false;
              try {
                var r = anchor.getBoundingClientRect();
                var cx = r.left + r.width / 2;
                var cy = r.top + r.height / 2;
                ['mouseover','mousedown','mouseup','click'].forEach(function(name) {
                  anchor.dispatchEvent(new MouseEvent(name,
                    {bubbles: true, cancelable: true, clientX: cx, clientY: cy, button: 0}));
                });
                return true;
              } catch (e) {
                try { anchor.click(); return true; } catch (e2) { return false; }
              }
            }
        """;
        var r = await session.ExecuteScriptAsync(js, null, ct);
        return r is true;
    }

    private static async Task<bool> TryFragmentClick(
        IBrowserSession session, string fragment, CancellationToken ct)
    {
        var js = $$"""
            return (function() {
              var frag = {{JsonSerializer.Serialize(fragment)}};
              var anchors = document.querySelectorAll('a[href]');
              for (var i = 0; i < anchors.length; i++) {
                if (anchors[i].href && anchors[i].href.indexOf(frag) !== -1) {
                  try {
                    var r = anchors[i].getBoundingClientRect();
                    var cx = r.left + r.width / 2;
                    var cy = r.top + r.height / 2;
                    ['mouseover','mousedown','mouseup','click'].forEach(function(n) {
                      anchors[i].dispatchEvent(new MouseEvent(n,
                        {bubbles: true, cancelable: true, clientX: cx, clientY: cy, button: 0}));
                    });
                    return true;
                  } catch (e) {
                    try { anchors[i].click(); return true; } catch (e2) {}
                  }
                }
              }
              return false;
            })();
        """;
        var r = await session.ExecuteScriptAsync(js, null, ct);
        return r is true;
    }

    private static async Task<bool> TryHostScanClick(
        IBrowserSession session, string host, CancellationToken ct)
    {
        var js = $$"""
            return (function() {
              var host = {{JsonSerializer.Serialize(host)}}.toLowerCase();
              var anchors = document.querySelectorAll('a[href]');
              for (var i = 0; i < anchors.length; i++) {
                try {
                  var u = new URL(anchors[i].href);
                  if (u.hostname.toLowerCase() === host
                      || u.hostname.toLowerCase().endsWith('.' + host)) {
                    var r = anchors[i].getBoundingClientRect();
                    var cx = r.left + r.width / 2;
                    var cy = r.top + r.height / 2;
                    ['mouseover','mousedown','mouseup','click'].forEach(function(n) {
                      anchors[i].dispatchEvent(new MouseEvent(n,
                        {bubbles: true, cancelable: true, clientX: cx, clientY: cy, button: 0}));
                    });
                    return true;
                  }
                } catch (e) {}
              }
              return false;
            })();
        """;
        var r = await session.ExecuteScriptAsync(js, null, ct);
        return r is true;
    }

    /// <summary>
    /// Pull a unique-ish 32-char substring out of the tracker URL.
    /// Skips the scheme + host (which collide between ads from the
    /// same vendor) and uses the middle of the path/query. Empty if
    /// the URL is short.
    /// </summary>
    private static string ExtractFragment(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var pathQuery = u.PathAndQuery;
        if (pathQuery.Length < 32) return pathQuery;
        // Take chars 8..40 — well past any common prefix.
        var start = Math.Min(8, pathQuery.Length - 32);
        return pathQuery.Substring(start, Math.Min(32, pathQuery.Length - start));
    }

    /// <summary>
    /// Unwrap a Google (or Google-Ads) tracker URL to its real
    /// destination. Google ads come back as
    /// <c>https://www.google.com/aclk?...&amp;adurl=https://realsite.com/...</c>
    /// — the OUTER host is always <c>www.google.com</c>, which would
    /// trip the own-domain guard against the SERP page host on every
    /// single ad. Extract the actual landing URL from <c>adurl</c>
    /// (ads) or <c>q</c> (organic) query params before any host-based
    /// gate runs. Returns the input unchanged when it isn't a
    /// recognised redirector.
    ///
    /// Recognised redirectors:
    ///   • <c>www.google.com/aclk</c> — main ad click redirector
    ///   • <c>www.google.com/url</c>  — organic result redirector
    ///   • <c>googleadservices.com/pagead/aclk</c> — alt ad redirect
    ///   • <c>www.googleadservices.com/pagead/aclk</c> — same, with www
    /// </summary>
    public static string UnwrapAdRedirect(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var host = u.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        var path = u.AbsolutePath.ToLowerInvariant();
        var isGoogleRedirector =
            (host == "google.com" && (path == "/aclk" || path == "/url"))
            || (host == "googleadservices.com" && path.StartsWith("/pagead/aclk"));
        if (!isGoogleRedirector) return url;
        try
        {
            // Manual query-string parse — avoids dragging in
            // System.Web.HttpUtility (not available by default in this
            // assembly's reference set). Two-pass: prefer adurl (ad
            // redirector), fall back to q (organic redirector).
            string? adurl = null, q = null;
            var query = u.Query.StartsWith("?") ? u.Query[1..] : u.Query;
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = pair[..eq];
                var val = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (string.Equals(key, "adurl", StringComparison.OrdinalIgnoreCase)) adurl = val;
                else if (string.Equals(key, "q", StringComparison.OrdinalIgnoreCase)) q ??= val;
            }
            var dest = adurl ?? q;
            if (string.IsNullOrEmpty(dest)) return url;
            // Validate that the unwrapped value is actually an absolute
            // URL — otherwise we'd silently swap in a path-only string
            // and break downstream Uri parsing. If not absolute, fall
            // back to the original (the guard then errs on the side of
            // rejecting, which is safer than letting a malformed URL
            // through unchecked).
            return Uri.TryCreate(dest, UriKind.Absolute, out _) ? dest : url;
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// Own-domain guard: returns true iff <paramref name="adHref"/>'s
    /// host equals the current page's host. Used to prevent the
    /// runner from clicking a "self-ad" (rare but real on some
    /// SERPs that surface the same domain you came from).
    ///
    /// The ad href is run through <see cref="UnwrapAdRedirect"/>
    /// first — Google's <c>www.google.com/aclk?...</c> wrapper would
    /// otherwise always match the SERP page host and reject every
    /// ad on Google as a "self-click".
    /// </summary>
    public static async Task<bool> IsSelfClickAsync(
        IBrowserSession session, string adHref, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(adHref)) return false;
        var unwrapped = UnwrapAdRedirect(adHref);
        if (!Uri.TryCreate(unwrapped, UriKind.Absolute, out var au)) return false;
        try
        {
            var pageUrl = await session.ExecuteScriptAsync(
                "return location.href;", null, ct) as string;
            if (string.IsNullOrEmpty(pageUrl)) return false;
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pu)) return false;
            return string.Equals(au.Host, pu.Host, StringComparison.OrdinalIgnoreCase)
                || au.Host.EndsWith("." + pu.Host, StringComparison.OrdinalIgnoreCase)
                || pu.Host.EndsWith("." + au.Host, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<AdRecord> MaterialiseAdList(object? raw)
    {
        var result = new List<AdRecord>();
        if (raw is null) return result;

        // Selenium returns IList<object> for arrays; each item is
        // typically a Dictionary<string, object>.
        if (raw is System.Collections.IEnumerable arr)
        {
            foreach (var item in arr)
            {
                if (item is not System.Collections.IDictionary dict) continue;
                var id     = ToInt(dict["id"]);
                var href   = dict["href"]?.ToString() ?? "";
                var title  = dict["title"]?.ToString();
                var disp   = dict["displayUrl"]?.ToString();
                if (string.IsNullOrEmpty(href)) continue;
                result.Add(new AdRecord
                {
                    StampId    = id,
                    Href       = href,
                    Title      = title,
                    DisplayUrl = disp,
                });
            }
        }
        return result;
    }

    private static int ToInt(object? v) => v switch
    {
        int i        => i,
        long l       => (int)l,
        double d     => (int)d,
        string s     => int.TryParse(s, out var x) ? x : 0,
        JsonElement el when el.ValueKind == JsonValueKind.Number => el.GetInt32(),
        _ => 0,
    };
}

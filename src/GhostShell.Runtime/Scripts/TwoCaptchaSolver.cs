// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// 2captcha (rucaptcha-compatible) integration. Submits a reCAPTCHA
/// v2 / hCaptcha task with the page sitekey + URL, polls
/// <c>/res.php</c> until ready, then injects the token into the
/// page's <c>g-recaptcha-response</c> textarea.
///
/// API key comes from <see cref="TwoCaptchaConfig.ApiKey"/> — empty
/// → falls back to manual solve (we never silently spam 2captcha
/// with anonymous requests).
///
/// Pricing / quota / 5-min poll cap is the user's contract with
/// 2captcha; this class doesn't try to be smart about it.
///
/// anticaptcha.com uses an almost-identical wire format; the v1
/// here can be cloned with a different base URL + slightly
/// different field names.
/// </summary>
public sealed class TwoCaptchaSolver : ICaptchaSolver
{
    private static readonly Uri BaseUri = new("https://2captcha.com/");

    private readonly HttpClient _http;
    private readonly TwoCaptchaConfig _cfg;
    private readonly ILogger<TwoCaptchaSolver> _log;

    public TwoCaptchaSolver(
        HttpClient http,
        TwoCaptchaConfig cfg,
        ILogger<TwoCaptchaSolver> log)
    {
        _http = http;
        _http.BaseAddress = BaseUri;
        _cfg = cfg;
        _log = log;
    }

    public string ProviderName => "2captcha";

    // Feature #3 — capability hints for CaptchaSolverRouter. Configured only
    // when a key is present; this v1 wire format handles reCAPTCHA v2 + hCaptcha.
    public bool IsAutomated => !string.IsNullOrWhiteSpace(_cfg.ApiKey);
    public bool CanHandle(string kind) => kind is "recaptcha" or "hcaptcha";

    public async Task<string?> DetectAsync(IBrowserSession session, CancellationToken ct = default)
    {
        // Reuse the legwork in ManualCaptchaSolver — same DOM patterns.
        // We don't share code via inheritance because the two
        // implementations conceptually serve different roles; instead
        // copy the small detector here. Worth the duplication to keep
        // each class freely deletable.
        const string Js = """
            return (function() {
              if (document.querySelector('iframe[src*="recaptcha"]')) return 'recaptcha';
              if (document.querySelector('iframe[src*="hcaptcha"]'))  return 'hcaptcha';
              if (document.querySelector('div.g-recaptcha'))           return 'recaptcha';
              if (document.querySelector('div.h-captcha'))             return 'hcaptcha';
              return null;
            })();
        """;
        try { return await session.ExecuteScriptAsync(Js, null, ct) as string; }
        catch { return null; }
    }

    public async Task<bool> SolveAsync(
        IBrowserSession session, string kind, TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_cfg.ApiKey))
        {
            _log.LogWarning("2captcha API key is empty — solve aborted; configure TwoCaptchaConfig.ApiKey");
            return false;
        }
        if (kind != "recaptcha" && kind != "hcaptcha")
        {
            _log.LogInformation("2captcha doesn't handle kind '{Kind}' — manual solve required", kind);
            return false;
        }

        // Pull sitekey + page URL from the live page.
        var info = await ExtractRecaptchaInfoAsync(session, kind, ct);
        if (info is null)
        {
            _log.LogWarning("Could not extract sitekey from page — 2captcha can't solve");
            return false;
        }

        // Submit task.
        string taskId;
        try
        {
            taskId = await SubmitTaskAsync(kind, info.Value.Sitekey, info.Value.PageUrl, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "2captcha task submit failed");
            return false;
        }
        _log.LogInformation("2captcha task submitted: id={Id}", taskId);

        // Poll for result. The 2captcha docs say min 5s before first
        // poll; cheaper to wait 8 to avoid the immediate "CAPCHA_NOT_READY".
        try { await Task.Delay(TimeSpan.FromSeconds(8), ct); }
        catch (OperationCanceledException) { return false; }

        var deadline = DateTime.UtcNow + timeout;
        string? token = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                token = await PollAsync(taskId, ct);
                if (token is not null) break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "2captcha poll threw — retrying");
            }
            // Jittered poll. The detect path uses 1.5-2.5s; 2captcha
            // says re-poll every 5s minimum. We do 5-7s.
            await Task.Delay(Random.Shared.Next(5000, 7001), ct);
        }
        if (token is null)
        {
            _log.LogWarning("2captcha solve timed out (task {Id})", taskId);
            return false;
        }

        // Inject token into the page's response field. Slight differ-
        // ences between recaptcha (textarea#g-recaptcha-response) and
        // hcaptcha (textarea[name=h-captcha-response]) — handle both.
        // audit CAPTCHA-08: prefix the IIFE with `return` so its result
        // actually reaches C# (per Selenium ExecuteScript semantics — see
        // the other scripts in this file), and report how many response
        // fields we populated so SolveAsync can tell a real injection from
        // a no-op (page had no g-recaptcha-response / h-captcha-response).
        var injectJs = $$"""
            return (function() {
              var token = {{JsonSerializer.Serialize(token)}};
              var filled = 0;
              var t1 = document.getElementById('g-recaptcha-response');
              if (t1) { t1.style.display = 'block'; t1.value = token; filled++; }
              var t2 = document.querySelector('textarea[name="h-captcha-response"]');
              if (t2) { t2.style.display = 'block'; t2.value = token; filled++; }
              // Many sites also listen for callback names registered
              // via grecaptcha.render({callback}); fire the standard
              // submit handler if present.
              if (window.___grecaptcha_cfg && window.___grecaptcha_cfg.clients) {
                try {
                  Object.keys(window.___grecaptcha_cfg.clients).forEach(function(k) {
                    var c = window.___grecaptcha_cfg.clients[k];
                    Object.values(c).forEach(function(o) {
                      Object.values(o || {}).forEach(function(p) {
                        if (p && typeof p.callback === 'function') {
                          try { p.callback(token); } catch (e) {}
                        }
                      });
                    });
                  });
                } catch (e) {}
              }
              return filled;
            })()
        """;
        // audit CAPTCHA-08: only claim success when at least one response
        // field was actually populated. Selenium marshals the JS number as
        // a long; tolerate other numeric shapes defensively.
        var injectResult = await session.ExecuteScriptAsync(injectJs, null, ct);
        var filledCount = injectResult switch
        {
            long l   => l,
            int i    => i,
            double d => (long)d,
            _        => 0L,
        };
        if (filledCount <= 0)
        {
            _log.LogWarning(
                "2captcha token NOT injected: no g-recaptcha-response / h-captcha-response "
                + "field found on page (kind={Kind}) — treating solve as failed so the caller can retry", kind);
            return false;
        }
        _log.LogInformation("2captcha token injected into {Count} field(s) (kind={Kind})", filledCount, kind);
        return true;
    }

    // audit CAPTCHA-06: instance (was static) so we can log a diagnostic
    // distinguishing "no captcha element on page" from "element found but
    // we couldn't read its marshalled shape".
    private async Task<(string Sitekey, string PageUrl)?> ExtractRecaptchaInfoAsync(
        IBrowserSession session, string kind, CancellationToken ct)
    {
        var attr = kind == "hcaptcha" ? "data-sitekey" : "data-sitekey";
        var sel  = kind == "hcaptcha" ? "div.h-captcha,iframe[src*=\"hcaptcha\"]"
                                      : "div.g-recaptcha,iframe[src*=\"recaptcha\"]";
        var js = $$"""
            return (function() {
              var el = document.querySelector({{JsonSerializer.Serialize(sel)}});
              if (!el) return null;
              var key = el.getAttribute({{JsonSerializer.Serialize(attr)}});
              if (!key && el.tagName === 'IFRAME') {
                // Pull sitekey out of the iframe src.
                var m = /[?&]k=([^&]+)/.exec(el.src) || /[?&]sitekey=([^&]+)/.exec(el.src);
                if (m) key = decodeURIComponent(m[1]);
              }
              return key ? {sitekey: key, url: location.href} : null;
            })();
        """;
        var raw = await session.ExecuteScriptAsync(js, null, ct);
        // audit CAPTCHA-06: raw == null is the legitimate "no captcha
        // element / no sitekey on this page" case — caller's warning is
        // accurate there, so stay quiet (debug only).
        if (raw is null)
        {
            _log.LogDebug("2captcha sitekey extraction: page returned null (no captcha element or no sitekey, kind={Kind})", kind);
            return null;
        }
        // audit CAPTCHA-06: an element WAS found and marshalled, but not as
        // an IDictionary (driver upgrade / hCaptcha variant / ReadOnly or
        // JObject shape). This is a breakage, not an absent captcha — log it
        // loudly with the actual runtime type so it's triageable instead of
        // masquerading as "could not extract sitekey".
        if (raw is not System.Collections.IDictionary dict)
        {
            _log.LogWarning(
                "2captcha sitekey extraction returned an unexpected shape ({Type}) — "
                + "Selenium marshalling may have changed; cannot read sitekey (kind={Kind})",
                raw.GetType().FullName, kind);
            return null;
        }
        var sitekey = dict["sitekey"]?.ToString();
        var url     = dict["url"]?.ToString();
        if (string.IsNullOrEmpty(sitekey) || string.IsNullOrEmpty(url))
        {
            // audit CAPTCHA-06: dictionary present but expected keys missing
            // (key-casing did not survive the round trip, or shape differs).
            _log.LogWarning(
                "2captcha sitekey extraction: dictionary present but sitekey/url unreadable "
                + "(keys=[{Keys}], kind={Kind})",
                string.Join(",", dict.Keys.Cast<object?>().Select(k => k?.ToString())), kind);
            return null;
        }
        return (sitekey, url);
    }

    private async Task<string> SubmitTaskAsync(
        string kind, string sitekey, string pageUrl, CancellationToken ct)
    {
        // POST with form body keeps the API key out of the URL —
        // off HTTP-server logs, off upstream-proxy access logs.
        // 2captcha accepts both GET and POST for in.php / res.php.
        var method = kind == "hcaptcha" ? "hcaptcha" : "userrecaptcha";
        // audit CAPTCHA-07: this dictionary holds the paid API key under
        // "key". It is a SECRET — never pass `form` (or its content) to a
        // logger or exception message. The provider error fields used below
        // (Status / Request) come from the *response* and never echo the
        // key, but always scrub through Redact() before surfacing them so a
        // future change can't leak it.
        var form = new Dictionary<string, string>
        {
            ["key"]     = _cfg.ApiKey,
            ["method"]  = method,
            ["sitekey"] = sitekey,
            ["pageurl"] = pageUrl,
            ["json"]    = "1",
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync("in.php", content, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TwoCaptchaResponse>(cancellationToken: ct);
        if (body is null || body.Status != 1)
            throw new InvalidOperationException(
                $"2captcha rejected task: status={body?.Status} request={Redact(body?.Request)}");
        return body.Request ?? throw new InvalidOperationException("2captcha returned empty request id");
    }

    private async Task<string?> PollAsync(string taskId, CancellationToken ct)
    {
        // audit CAPTCHA-07: as in SubmitTaskAsync, "key" here is the secret
        // API key — never log or serialize `form`.
        var form = new Dictionary<string, string>
        {
            ["key"]    = _cfg.ApiKey,
            ["action"] = "get",
            ["id"]     = taskId,
            ["json"]   = "1",
        };
        using var content = new FormUrlEncodedContent(form);
        using var resp = await _http.PostAsync("res.php", content, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TwoCaptchaResponse>(cancellationToken: ct);
        if (body is null) return null;
        if (body.Status == 1) return body.Request;
        // status=0 + request="CAPCHA_NOT_READY" is the normal "still
        // working" state. Anything else with status=0 is a hard
        // error (ERROR_KEY_DOES_NOT_EXIST, ERROR_NO_SLOT_AVAILABLE,
        // etc.) — log and bail.
        if (!string.IsNullOrEmpty(body.Request)
            && body.Request != "CAPCHA_NOT_READY"
            && !body.Request.StartsWith("CAPCHA_", StringComparison.Ordinal))
        {
            // audit CAPTCHA-07: scrub the provider error string before it
            // hits the log / exception. 2captcha's error codes don't carry
            // the key today, but Redact() is the enforced guard rail so an
            // unexpected echo (or a future change) can never disclose it.
            _log.LogWarning("2captcha returned error: {Err}", Redact(body.Request));
            throw new InvalidOperationException(Redact(body.Request));
        }
        return null;
    }

    // audit CAPTCHA-07: single choke point for surfacing any provider-
    // supplied string into a log or exception. Removes the configured API
    // key (and its URL-encoded form) if it ever appears, so the secret can
    // never be written to a diagnostics-shipped log file regardless of what
    // 2captcha echoes back or what tracing a future maintainer adds.
    private string? Redact(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var key = _cfg.ApiKey;
        if (string.IsNullOrEmpty(key)) return s;
        s = s.Replace(key, "***REDACTED-2CAPTCHA-KEY***", StringComparison.Ordinal);
        // Defend against the URL-encoded form too (in case a request body
        // ever leaks through a handler), but only when it differs from the
        // raw key to avoid a pointless second pass.
        var encoded = Uri.EscapeDataString(key);
        if (!string.Equals(encoded, key, StringComparison.Ordinal))
            s = s.Replace(encoded, "***REDACTED-2CAPTCHA-KEY***", StringComparison.Ordinal);
        return s;
    }

    private sealed class TwoCaptchaResponse
    {
        [JsonPropertyName("status")]  public int Status { get; init; }
        [JsonPropertyName("request")] public string? Request { get; init; }
    }
}

/// <summary>API-key + endpoint config for <see cref="TwoCaptchaSolver"/>.</summary>
public sealed class TwoCaptchaConfig
{
    /// <summary>2captcha account API key (empty = disable, fall back to manual).</summary>
    public string ApiKey { get; init; } = "";
}

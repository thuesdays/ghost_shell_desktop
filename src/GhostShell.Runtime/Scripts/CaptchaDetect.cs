// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Services;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Shared captcha detection + sitekey extraction, reused by every solver and
/// the router so they all recognise the same surface. Covers what the product
/// actually meets in the field:
///   • reCAPTCHA v2 (checkbox / invisible)
///   • reCAPTCHA v3 (score-based — sitekey present, no widget)
///   • hCaptcha
///   • Cloudflare Turnstile
///   • Google's "sorry" / "unusual traffic" network-block interstitial
///     (NOT a solvable widget — surfaced so the caller can rotate the IP
///      instead of burning solver credits).
/// </summary>
public static class CaptchaDetect
{
    /// <summary>JS that classifies the current page. Returns one of the kind
    /// strings above, or null when no captcha/interstitial is present.</summary>
    public const string DetectKindJs = """
        return (function() {
          var href = location.href || '';
          // Google network-block interstitial — page IS the block, not a widget.
          if (/\/sorry\/index/.test(href) ||
              document.querySelector('form#captcha-form') ||
              /unusual traffic|necessary to verify|not a robot from this network/i
                .test(document.body ? document.body.innerText : '')) {
            // Distinguish: a /sorry page that embeds a reCAPTCHA is still
            // best handled as 'sorry' (rotate IP) — solving rarely sticks.
            return 'sorry';
          }
          if (document.querySelector('.cf-turnstile, iframe[src*="challenges.cloudflare.com"], input[name="cf-turnstile-response"]'))
            return 'turnstile';
          if (document.querySelector('iframe[src*="hcaptcha"], div.h-captcha, textarea[name="h-captcha-response"]'))
            return 'hcaptcha';
          // reCAPTCHA: widget (v2) or bare sitekey holder (v3).
          var rc = document.querySelector('iframe[src*="recaptcha"], div.g-recaptcha, #g-recaptcha-response, [data-sitekey]');
          if (rc) {
            // v3 has no visible challenge frame; detect the grecaptcha.execute path.
            var v3 = !!document.querySelector('script[src*="render="]') ||
                     (window.grecaptcha && !document.querySelector('iframe[src*="bframe"]') &&
                      !document.querySelector('.g-recaptcha'));
            return v3 ? 'recaptcha_v3' : 'recaptcha';
          }
          return null;
        })();
    """;

    /// <summary>Detect the captcha kind on the current page; null if none.</summary>
    public static async Task<string?> DetectAsync(IBrowserSession session, CancellationToken ct = default)
    {
        try { return await session.ExecuteScriptAsync(DetectKindJs, null, ct) as string; }
        catch { return null; }
    }

    /// <summary>Extracted widget parameters needed to submit a solve task.</summary>
    public readonly record struct SiteKeyInfo(string Sitekey, string PageUrl, string? Action);

    /// <summary>Pull the sitekey (+ page URL, + v3 action when present) for the
    /// given kind. Null when no sitekey can be found.</summary>
    public static async Task<SiteKeyInfo?> ExtractSiteKeyAsync(
        IBrowserSession session, string kind, CancellationToken ct = default)
    {
        var selector = kind switch
        {
            "turnstile"    => ".cf-turnstile,[data-sitekey],iframe[src*=\"challenges.cloudflare.com\"]",
            "hcaptcha"     => "div.h-captcha,[data-sitekey],iframe[src*=\"hcaptcha\"]",
            _              => "div.g-recaptcha,[data-sitekey],iframe[src*=\"recaptcha\"]",
        };
        var js = $$"""
            return (function() {
              var el = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!el) return null;
              var key = el.getAttribute('data-sitekey') || el.getAttribute('data-site-key');
              if (!key && el.tagName === 'IFRAME') {
                var m = /[?&](?:k|sitekey)=([^&]+)/.exec(el.src || '');
                if (m) key = decodeURIComponent(m[1]);
              }
              var action = el.getAttribute('data-action') || null;
              return key ? {sitekey: key, url: location.href, action: action} : null;
            })();
        """;
        object? raw;
        try { raw = await session.ExecuteScriptAsync(js, null, ct); }
        catch { return null; }
        if (raw is not System.Collections.IDictionary d) return null;
        var sitekey = d["sitekey"]?.ToString();
        var url     = d["url"]?.ToString();
        if (string.IsNullOrEmpty(sitekey) || string.IsNullOrEmpty(url)) return null;
        return new SiteKeyInfo(sitekey, url, d["action"]?.ToString());
    }

    /// <summary>Inject a solved token into every response field the page might
    /// read (reCAPTCHA, hCaptcha, Turnstile) and fire reCAPTCHA client
    /// callbacks. Returns the number of fields populated.</summary>
    public static async Task<int> InjectTokenAsync(
        IBrowserSession session, string token, CancellationToken ct = default)
    {
        var js = $$"""
            return (function() {
              var token = {{JsonSerializer.Serialize(token)}};
              var filled = 0;
              function set(sel) {
                document.querySelectorAll(sel).forEach(function(t) {
                  try { t.style.display = 'block'; } catch (e) {}
                  t.value = token; filled++;
                  try { t.dispatchEvent(new Event('input', {bubbles:true})); } catch (e) {}
                });
              }
              set('#g-recaptcha-response');
              set('textarea[name="g-recaptcha-response"]');
              set('textarea[name="h-captcha-response"]');
              set('input[name="cf-turnstile-response"]');
              set('input[name="g-recaptcha-response"]');
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
            })();
        """;
        try
        {
            var r = await session.ExecuteScriptAsync(js, null, ct);
            return r switch { long l => (int)l, int i => i, double dd => (int)dd, _ => 0 };
        }
        catch { return 0; }
    }
}

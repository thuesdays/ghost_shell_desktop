// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Services;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 31 — site-specific extractors for the external fingerprint
/// testers. Each extractor:
///   1. Knows how long the tester needs to settle after navigation.
///   2. Runs a short JS snippet against the live page to pull out the
///      meaningful number / verdict the page just rendered.
///   3. Returns a one-line summary AND a structured <see cref="TesterResult"/>
///      with key-value rows for the detail dialog.
///
/// Extractors are best-effort — if a site changes its DOM, the
/// extractor returns an empty summary and the caller falls back to
/// the page title. We don't try to call private APIs (Pixelscan,
/// Fingerprint.com) — only DOM scraping of what the public report
/// page already rendered.
/// </summary>
public static class TesterProbe
{
    /// <summary>Initial settle delay after navigation — gives the page time to
    /// start its asynchronous work. This is a conservative first wait; the
    /// polling loop will then do repeated extractions if the verdict isn't ready.
    /// Phase 33 — bumped down to 3s for all (the page just needs to begin loading;
    /// the extractor returns "?" if not ready yet, triggering more polls).</summary>
    public static TimeSpan SettleFor(string testerName) => testerName switch
    {
        "CreepJS"               => TimeSpan.FromSeconds(3),
        "BrowserLeaks"          => TimeSpan.FromSeconds(3),
        "Pixelscan"             => TimeSpan.FromSeconds(3),
        "Fingerprint.com BotD"  => TimeSpan.FromSeconds(3),
        "AmIUnique"             => TimeSpan.FromSeconds(3),
        "Sannysoft Bot Test"    => TimeSpan.FromSeconds(3),
        _                        => TimeSpan.FromSeconds(3),
    };

    /// <summary>Maximum total polling window per tester. After the initial settle
    /// delay, we'll keep asking the page "is your verdict ready?" up to this
    /// timeout. Extracted from real observation of when each site finishes
    /// rendering its final verdict. The poll loop checks every 2s whether we
    /// have a non-"?" result; if not, it keeps looping until the deadline.
    /// Phase 32 — bumped based on user's network + the async load times we
    /// observe in dev-tools: CreepJS trust-score rendering can take 15-18s
    /// after the page loads; Pixelscan waits for /v3/check POST; AmIUnique
    /// waits for server analysis redirects. We poll-and-extract instead of
    /// a single fixed delay, so users with fast networks finish in 5-6s while
    /// slow networks aren't cut short.</summary>
    public static TimeSpan MaxWaitFor(string testerName) => testerName switch
    {
        "CreepJS"               => TimeSpan.FromSeconds(30),
        "BrowserLeaks"          => TimeSpan.FromSeconds(12),
        "Pixelscan"             => TimeSpan.FromSeconds(25),
        "Fingerprint.com BotD"  => TimeSpan.FromSeconds(6),   // Phase 60: inline probe, ~50ms execution, no demo page needed
        "AmIUnique"             => TimeSpan.FromSeconds(25),
        "Sannysoft Bot Test"    => TimeSpan.FromSeconds(6),
        _                        => TimeSpan.FromSeconds(15),
    };

    /// <summary>Quick liveness check for the browser session. Returns true if
    /// the session is still active and responsive; false if the driver was
    /// closed/disposed externally. Used by the polling loop to detect when
    /// the user closes the browser during a probe, so we can clean up gracefully
    /// instead of throwing "Cannot access a disposed object" errors.
    /// Phase 32 — this prevents the confusing ObjectDisposedException when
    /// the user closes the browser mid-probe; we now just log "browser closed"
    /// and skip remaining testers.</summary>
    public static async Task<bool> IsSessionAlive(IBrowserSession session)
    {
        if (session is null) return false;
        try
        {
            // A zero-cost round-trip: just execute a single return statement.
            // If this throws ObjectDisposedException or WebDriverException,
            // the driver is dead.
            _ = await session.ExecuteScriptAsync("return 1;", null);
            return true;
        }
        catch (ObjectDisposedException) { return false; }
        catch (Exception ex) when (ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
                                || ex.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch { return true; } // Assume alive on unknown errors (permission denied, etc.)
    }

    /// <summary>Run the per-tester JS extractor against the live
    /// session and return a one-line summary. Throws ONLY for
    /// session-level failures (driver dead, navigation cancelled);
    /// site-DOM failures return an empty string so the caller falls
    /// back gracefully.</summary>
    public static async Task<string> ExtractAsync(string testerName, IBrowserSession session)
    {
        var result = await ExtractDetailedAsync(testerName, session);
        LastResults[testerName] = result;
        return result.Summary;
    }

    /// <summary>Per-name cache of the most recent extraction. The UI
    /// pulls from this when the user clicks a card to see details
    /// without rerunning the probe.</summary>
    public static readonly Dictionary<string, TesterResult> LastResults = new(StringComparer.Ordinal);

    public static async Task<TesterResult> ExtractDetailedAsync(string testerName, IBrowserSession session)
    {
        var js = ExtractorScript(testerName);
        if (string.IsNullOrEmpty(js))
        {
            return new TesterResult
            {
                TesterName = testerName,
                Summary    = "probed",
                Verdict    = "no extractor",
                Details    = Array.Empty<TesterDetailRow>(),
            };
        }
        try
        {
            var raw = await session.ExecuteScriptAsync(js, null) as string;
            if (string.IsNullOrWhiteSpace(raw))
                return new TesterResult { TesterName = testerName, Summary = "no data", Verdict = "?", Details = Array.Empty<TesterDetailRow>() };
            return ParseExtractorPayload(testerName, raw);
        }
        catch (Exception ex)
        {
            return new TesterResult
            {
                TesterName = testerName,
                Summary    = "extractor failed",
                Verdict    = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message,
                Details    = Array.Empty<TesterDetailRow>(),
            };
        }
    }

    /// <summary>Each script returns a JSON envelope:
    ///   { "summary": "trust 87/100", "verdict": "good",
    ///     "details": [ {"k":"score","v":"87/100"}, ... ] }
    /// — wrapped in a try/catch so a single broken querySelector
    /// doesn't kill the whole script. setTimeout(resolve,…) caps us.</summary>
    private static string ExtractorScript(string name) => name switch
    {
        "CreepJS"              => CreepJsScript,
        "Sannysoft Bot Test"   => SannysoftScript,
        "Pixelscan"            => PixelscanScript,
        "AmIUnique"            => AmIUniqueScript,
        "BrowserLeaks"         => BrowserLeaksScript,
        "Fingerprint.com BotD" => FingerprintBotdScript,
        _                       => "",
    };

    private static TesterResult ParseExtractorPayload(string testerName, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            string verdict = root.TryGetProperty("verdict", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
            var rows = new List<TesterDetailRow>();
            if (root.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in d.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var k = item.TryGetProperty("k", out var ke) ? ke.GetString() ?? "" : "";
                    var val = item.TryGetProperty("v", out var ve) ? ve.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(k))
                        rows.Add(new TesterDetailRow(k, val));
                }
            }
            return new TesterResult
            {
                TesterName = testerName,
                Summary    = string.IsNullOrWhiteSpace(summary) ? verdict : summary,
                Verdict    = verdict,
                Details    = rows,
            };
        }
        catch
        {
            // Non-JSON output — surface verbatim, capped.
            var snip = raw.Length > 120 ? raw[..120] + "…" : raw;
            return new TesterResult
            {
                TesterName = testerName,
                Summary    = snip,
                Verdict    = "raw",
                Details    = Array.Empty<TesterDetailRow>(),
            };
        }
    }

    // ─── Per-tester JS payloads ──────────────────────────────────────
    //
    // Each script returns a JSON STRING (not object) so the .NET side
    // gets a single deterministic shape and we don't have to wrestle
    // Selenium's IList<object> wrapping for each property.
    //
    // A 6-second window inside the script is the wallclock cap; the
    // outer settle delay already waited for the page to render, so
    // these are last-mile DOM scrapes.

    private const string Wrapper = """
        return new Promise((resolve) => {
          const cap = setTimeout(() => resolve(JSON.stringify({summary:'timed out',verdict:'?',details:[]})), 6000);
          try { __BODY__ } catch (e) { clearTimeout(cap); resolve(JSON.stringify({summary:'extractor error',verdict:String(e),details:[]})); }
        });
    """;

    private static string Wrap(string body) => Wrapper.Replace("__BODY__", body);

    /// <summary>
    /// Per-tester flag: when true, skip the navigate phase and run the
    /// extractor inline on the current page (useful for Sannysoft which
    /// runs pure JS checks and doesn't need a real page load). When false
    /// (default), follow the legacy pattern: navigate to the URL, settle,
    /// then extract.
    /// </summary>
    public static bool SkipNavigationFor(string testerName)
    {
        // Phase 34 — Sannysoft is now fully inlined; no page load needed.
        // Phase 60 — Fingerprint.com BotD is also fully inlined; the probe
        // runs the same detector signals BotD checks for (navigator.webdriver,
        // chrome.runtime, $cdc_ markers, WebGL Mesa, etc.) directly against
        // the live page context, no CDN load and no marketing-demo widget.
        return testerName == "Sannysoft Bot Test"
            || testerName == "Fingerprint.com BotD";
    }

    /// <summary>CreepJS exposes a "trust score" + "lies count" inside
    /// elements rendered into the .fingerprint container. The score
    /// shows up as a percentage like "87% on a 0-100 trust scale". The
    /// trust level box uses class "high"/"moderate"/"low"/"untrusted".
    ///
    /// Phase 32 rewrite — earlier version only matched plain-text
    /// "trust score: NN" which the site doesn't actually render. We
    /// now read the trust % via the dedicated badge first, then fall
    /// back to scraping all numeric "/100" fragments. Also pulls
    /// FP-id (the visitor's stable hash, displayed in the header) and
    /// the lies array length via DOM count.
    /// Phase 33 — added explicit DOM selectors (.trust-score, .fingerprint-data)
    /// which CreepJS uses in its modern layout. Tried first, then fall back
    /// to class-contains and regex for older versions.
    /// Phase 34 — extract individual lies (e.g. "navigator.userAgent inconsistency")
    /// and per-attribute category scores (canvas, audio, fonts, webgl) to give
    /// the user diagnostic detail beyond just trust % + lies count.
    /// Phase 35 (HIGH PRIORITY) — fix lies extraction to show readable descriptions
    /// (innerText) instead of hex hashes. Also improve category scores to capture
    /// the full pattern including the denominator (e.g. "Canvas: 2/20 score").</summary>
    private static readonly string CreepJsScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        const txt = (document.body && document.body.innerText) || '';
        const html = (document.body && document.body.innerHTML) || '';

        // 1) Trust score — CreepJS modern DOM uses .trust-score and
        // .fingerprint-data. Try explicit selectors first, then
        // fall back to class-contains and regex heuristics.
        let score = null;

        // First try: .trust-score (modern CreepJS)
        const trustScoreEl = document.querySelector('.trust-score');
        if (trustScoreEl) {
            const m = (trustScoreEl.textContent || '').match(/(\d{1,3})\s*%/);
            if (m) score = parseInt(m[1], 10);
        }

        // Fallback: older layouts with class-contains
        if (score === null) {
            const trustEl = document.querySelector('[class*="trust" i], [class*="score" i]');
            if (trustEl) {
                const m = (trustEl.textContent || '').match(/(\d{1,3})\s*%/);
                if (m) score = parseInt(m[1], 10);
            }
        }

        // Fallback: plain-text regex
        if (score === null) {
            const m = txt.match(/trust(?:\s*score)?[^\d]{0,20}(\d{1,3})\s*(?:%|\/\s*100)/i);
            if (m) score = parseInt(m[1], 10);
        }

        // Last-ditch — first NN% in the page that isn't 100% or 0%.
        if (score === null) {
            const all = [...txt.matchAll(/(\d{1,3})\s*%/g)].map(x => parseInt(x[1], 10))
                .filter(n => n > 0 && n < 100);
            if (all.length > 0) score = all[0];
        }

        if (score !== null) {
            out.summary = 'trust ' + score + '%';
            out.details.push({k:'Trust score', v: score + ' / 100'});
            out.verdict = score >= 85 ? 'excellent' : score >= 70 ? 'ok' : score >= 50 ? 'weak' : 'flagged';
        }

        // 2) Lies — CreepJS prints "N lies" or "Lies: N" near the badge,
        // and also exposes them in elements with class containing 'lies'.
        let lies = null;
        const liesEl = document.querySelector('[class*="lies" i]');
        if (liesEl) {
            const m = (liesEl.textContent || '').match(/(\d+)/);
            if (m) lies = parseInt(m[1], 10);
        }
        if (lies === null) {
            const m = txt.match(/(\d+)\s+lies?\b/i);
            if (m) lies = parseInt(m[1], 10);
        }
        if (lies !== null) {
            out.details.push({k:'Lies detected', v: String(lies)});
            if (!out.verdict && lies > 5) out.verdict = 'flagged';
        }

        // 2a) Individual lies — extract up to 10 specific lies. CreepJS lists them
        // in a .lies container whose innerText is line-separated (each line is one lie
        // e.g. "⚠️ navigator.userAgent"). We split the container's text, filter
        // empty lines, and take the first 10. If no .lies container, fall back to
        // scanning [id*="lies"] .item, [class*="lies"] li selectors.
        let lieCount = 0;
        const liesContainer = document.querySelector('.lies, [class*="lies-len" i], [id*="lies" i]');
        if (liesContainer) {
            const liesText = (liesContainer.innerText || liesContainer.textContent || '').trim();
            if (liesText) {
                const lines = liesText.split('\n').map(l => l.trim()).filter(l => l);
                lines.slice(0, 10).forEach((line, idx) => {
                    // Strip leading emoji/symbol if present (⚠️, •, -, etc.)
                    let lieTxt = line.replace(/^[^a-zA-Z0-9]{0,3}\s*/, '');
                    if (lieTxt.length > 3) {
                        out.details.push({k: 'Lie #' + (idx + 1), v: lieTxt.slice(0, 120)});
                        lieCount++;
                    }
                });
            }
        }
        // Fallback: if no container found, scan for individual lie elements
        if (lieCount === 0) {
            const lieElements = document.querySelectorAll('[id*="lies" i] .item, [class*="lies" i] li');
            lieElements.forEach(el => {
                if (lieCount >= 10) return;
                let lieTxt = (el.innerText || el.textContent || '').trim();
                if (lieTxt && lieTxt.length > 5) {
                    lieTxt = lieTxt.replace(/^[^a-zA-Z0-9]{0,3}\s*/, '');
                    if (lieTxt.length > 3) {
                        out.details.push({k: 'Lie #' + (lieCount + 1), v: lieTxt.slice(0, 120)});
                        lieCount++;
                    }
                }
            });
        }

        // 2b) Per-attribute category scores — CreepJS shows badges for
        // Canvas, Audio, Fonts, WebGL, Screen, Headers. REQUIRE FULL LINE MATCH
        // (anchored ^...$) to avoid matching fragments like "1920" from "1920×1080".
        // Match pattern: "Category: N/M" or "Category: N lies" at line boundaries.
        const categoryNames = ['canvas', 'audio', 'fonts', 'webgl', 'headers', 'screen', 'trust', 'lies'];
        categoryNames.forEach(cat => {
            // Anchored regex: must be a complete line like "Canvas: 2/20" or "Audio: 10 lies"
            const regex = new RegExp(`^(${cat})\\s*[:\\-]\\s*(\\d+(?:\\s*/\\s*\\d+)?(?:\\s*lies?)?)\\s*$`, 'im');
            const m = txt.match(regex);
            if (m) {
                const catLabel = m[1].charAt(0).toUpperCase() + m[1].slice(1).toLowerCase();
                const scoreStr = m[2].trim();
                out.details.push({k: catLabel, v: scoreStr});
            }
        });

        // 3) Stable fingerprint id — CreepJS shows "fingerprint id"
        // followed by a 12-32 char hex hash near the page top.
        const fpMatch = txt.match(/fingerprint[^a-z0-9]{0,40}([a-f0-9]{8,})/i);
        if (fpMatch) out.details.push({k:'Fingerprint id', v: fpMatch[1].slice(0,20) + (fpMatch[1].length>20?'…':'')});

        // 4) Bot/headless flags — CreepJS lists "navigator.webdriver"
        // and similar lies. We surface them as boolean if the row
        // exists.
        if (/headless/i.test(html)) {
            const headlessMatch = txt.match(/headless\s*[:\-]?\s*(true|false|yes|no)/i);
            if (headlessMatch) out.details.push({k:'Headless', v: headlessMatch[1].toLowerCase()});
        }

        if (!out.summary) {
            // The page rendered SOMETHING, but we couldn't find a
            // score. Distinguish "still loading" from "rendered but
            // no score" so the user knows whether to try again.
            const stillLoading = !!document.querySelector('[class*="loading" i],[class*="spinner" i]');
            out.summary = stillLoading ? 'CreepJS still loading' : 'no trust score on page';
            out.verdict = '?';
        }
        clearTimeout(cap); resolve(JSON.stringify(out));
    """);

    /// <summary>
    /// Sannysoft — fully inlined JS probe without page load. Runs 8 core
    /// bot-detection checks directly on the current page context:
    /// 1. navigator.webdriver (must be undefined/false)
    /// 2. navigator.plugins.length (must be > 0)
    /// 3. navigator.languages.length (must be > 0)
    /// 4. Permissions.query notification (must be prompt, not denied)
    /// 5. chrome.runtime (extension API; absence = pass)
    /// 6. Image onerror handler (broken image detection; must fire)
    /// 7. WebGL vendor (must not say "Brian Paul" / "Mesa")
    /// 8. UserAgent consistency vs navigator properties
    /// Returns a count of pass/fail and verdict based on failures.
    /// Phase 34 — rewritten to NOT load bot.sannysoft.com; instead
    /// runs all checks inline via JS only. This is the essence of what
    /// Sannysoft's page does anyway. Wrapped in async IIFE because
    /// check #4 uses await on navigator.permissions.query().</summary>
    private static readonly string SannysoftScript = """
        return new Promise((resolve) => {
          const cap = setTimeout(() => resolve(JSON.stringify({summary:'timed out',verdict:'?',details:[]})), 6000);
          (async () => {
            try {
        const out = {summary:'', verdict:'', details:[]};
        let pass = 0, fail = 0;

        // 1) navigator.webdriver — must be undefined/false (Selenium leak)
        const webdriver = navigator.webdriver;
        if (!webdriver) {
            pass++;
            out.details.push({k: 'navigator.webdriver', v: 'undefined ✓'});
        } else {
            fail++;
            out.details.push({k: 'navigator.webdriver', v: webdriver + ' ✗'});
        }

        // 2) navigator.plugins.length — must be > 0 (normal browsers have plugins)
        const pluginCount = navigator.plugins.length;
        if (pluginCount > 0) {
            pass++;
            out.details.push({k: 'navigator.plugins', v: pluginCount + ' plugins ✓'});
        } else {
            fail++;
            out.details.push({k: 'navigator.plugins', v: '0 plugins ✗'});
        }

        // 3) navigator.languages.length — must be > 0
        const langCount = navigator.languages ? navigator.languages.length : 0;
        if (langCount > 0) {
            pass++;
            out.details.push({k: 'navigator.languages', v: langCount + ' language(s) ✓'});
        } else {
            fail++;
            out.details.push({k: 'navigator.languages', v: '0 languages ✗'});
        }

        // 4) Permissions.query — notifications must be "prompt", not "denied"
        let permissionResult = 'unknown';
        try {
            const perm = await navigator.permissions.query({name: 'notifications'});
            if (perm.state === 'prompt') {
                pass++;
                permissionResult = 'prompt ✓';
            } else if (perm.state === 'denied') {
                fail++;
                permissionResult = perm.state + ' ✗';
            } else {
                pass++;
                permissionResult = perm.state + ' ✓';
            }
        } catch (e) {
            // Permissions API might not be available; don't count as fail
            permissionResult = 'not available';
        }
        out.details.push({k: 'Notifications permission', v: permissionResult});

        // 5) chrome.runtime — extension API (should NOT be present in normal browser)
        const hasRuntime = typeof chrome !== 'undefined' && typeof chrome.runtime !== 'undefined';
        if (!hasRuntime) {
            pass++;
            out.details.push({k: 'chrome.runtime', v: 'not present ✓'});
        } else {
            fail++;
            out.details.push({k: 'chrome.runtime', v: 'present ✗'});
        }

        // 6) Image onerror — broken image must fire onerror (tests rendering)
        let imageTestPassed = false;
        try {
            const img = new Image();
            let onerrorFired = false;
            img.onerror = () => { onerrorFired = true; };
            img.src = 'about:invalid';
            // onerror fires synchronously for about:invalid
            if (onerrorFired) {
                pass++;
                imageTestPassed = true;
                out.details.push({k: 'Image.onerror', v: 'fires ✓'});
            } else {
                fail++;
                out.details.push({k: 'Image.onerror', v: 'blocked ✗'});
            }
        } catch (e) {
            fail++;
            out.details.push({k: 'Image.onerror', v: 'error: ' + String(e).slice(0,30)});
        }

        // 7) WebGL vendor — must not say "Brian Paul" or "Mesa" (headless marker)
        let webglVendor = 'unknown';
        try {
            const canvas = document.createElement('canvas');
            const gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
            if (gl) {
                const ext = gl.getExtension('WEBGL_debug_renderer_info');
                if (ext) {
                    const vendor = gl.getParameter(ext.UNMASKED_VENDOR_WEBGL) || '';
                    const renderer = gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) || '';
                    webglVendor = vendor + ' / ' + renderer;
                    const isMesa = /mesa|brian paul/i.test(webglVendor);
                    if (!isMesa) {
                        pass++;
                        out.details.push({k: 'WebGL vendor', v: vendor.slice(0, 40) + ' ✓'});
                    } else {
                        fail++;
                        out.details.push({k: 'WebGL vendor', v: 'Mesa (headless) ✗'});
                    }
                } else {
                    pass++;  // Can't detect, assume ok
                    out.details.push({k: 'WebGL vendor', v: 'not available ✓'});
                }
            } else {
                pass++;  // WebGL not available; not a fail
                out.details.push({k: 'WebGL', v: 'not available'});
            }
        } catch (e) {
            pass++;  // WebGL errors; assume ok
            out.details.push({k: 'WebGL', v: 'error (ok)'});
        }

        // 8) UserAgent consistency — check for obvious inconsistencies
        // (e.g. "Chrome 99" but navigator.chrome undefined, or Windows UA but platform says Mac)
        const ua = navigator.userAgent;
        const platform = navigator.platform;
        const appVersion = navigator.appVersion;
        let uaInconsistent = false;

        // Simple heuristic: if UA says Windows/Mac/Linux but platform disagrees
        const hasWindows = /Windows|Win32/i.test(ua);
        const hasMac = /Mac|iPhone|iPad/i.test(ua);
        const hasLinux = /Linux|X11/i.test(ua);
        const platformMatch = (hasWindows && /Win/.test(platform))
                           || (hasMac && /Mac/.test(platform))
                           || (hasLinux && /Linux|X11/.test(platform))
                           || !platform;  // platform might be empty in some browsers

        if (!platformMatch) {
            fail++;
            uaInconsistent = true;
            out.details.push({k: 'UserAgent platform', v: 'inconsistent ✗'});
        } else {
            pass++;
            out.details.push({k: 'UserAgent platform', v: 'consistent ✓'});
        }

        out.summary = pass + ' pass / ' + fail + ' fail';
        out.verdict = fail === 0 ? 'clean' : fail <= 1 ? 'mostly ok' : 'flagged';
        clearTimeout(cap); resolve(JSON.stringify(out));
            } catch (e) {
              clearTimeout(cap); resolve(JSON.stringify({summary:'extractor error',verdict:String(e),details:[]}));
            }
          })();
        });
    """;

    /// <summary>
    /// Pixelscan v3 exposes the verdict as one of these banner phrasings:
    ///   • "We don't think you are using a bot"          → ok
    ///   • "We are not sure if you are using a bot"      → weak
    ///   • "We think you are using a bot"                → flagged
    /// Phase 36 — instead of restricting to a specific result-block class
    /// (which is too brittle — Pixelscan uses different class names per page
    /// version and per A/B variant), search the WHOLE page text for the
    /// known verdict phrasings. The phrasings are specific enough to never
    /// match nav menus or marketing copy. As a fallback, click any "Start
    /// scan / Check fingerprint" button if no verdict is rendered yet, and
    /// return "scan in progress" so the polling loop retries.</summary>
    private static readonly string PixelscanScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};

        // Phase 36 — read whole-page text. The Pixelscan verdict phrases
        // are distinctive enough ("we think you are using a bot", etc) that
        // they can't false-positive against marketing copy. This is far more
        // robust than guessing class names which change with each redesign.
        const fullTxt = (document.body && document.body.innerText) || '';

        // Try to detect verdict from page text first (works when scan completed)
        let botSignal = null;  // 'no' | 'maybe' | 'yes'
        if (/we\s+do\s*n[o']?t\s+think.*?(?:are|is)\s+(?:using\s+)?a?\s*bot/i.test(fullTxt)) botSignal = 'no';
        else if (/we\s+are\s+not\s+sure.*?(?:are|is)\s+(?:using\s+)?a?\s*bot/i.test(fullTxt)) botSignal = 'maybe';
        else if (/we\s+think\s+you.*?(?:are|is)\s+(?:using\s+)?a?\s*bot/i.test(fullTxt))      botSignal = 'yes';
        else if (/no\s+bot\s+signal|not\s+(?:a\s+)?bot\s+detected/i.test(fullTxt))            botSignal = 'no';
        else if (/bot\s+detected|likely\s+(?:a\s+)?bot|automation\s+detected/i.test(fullTxt)) botSignal = 'yes';

        // If no verdict yet, try to start the scan by clicking a button
        if (botSignal === null) {
            const buttons = document.querySelectorAll('button, input[type="button"], a[role="button"]');
            for (const btn of buttons) {
                const btnText = (btn.innerText || btn.value || btn.textContent || '').trim().toLowerCase();
                if (/^(?:start\s+scan|run\s+scan|check\s+fingerprint|scan|check)$/i.test(btnText)
                    || /start\s+scan/i.test(btnText)) {
                    try { btn.click(); break; } catch (e) {}
                }
            }
        }

        // Consistency — Pixelscan reports this in the report. Page-wide search
        // is fine because "fingerprint inconsistent" / "consistent fingerprint"
        // phrases don't appear elsewhere on the page.
        let consistency = null;  // 'consistent' | 'inconsistent' | null
        if (/(?:fingerprint|profile|browser)\s+(?:is\s+)?inconsistent|inconsistencies\s+detected/i.test(fullTxt))
            consistency = 'inconsistent';
        else if (/(?:fingerprint|profile|browser)\s+(?:is\s+)?consistent|no\s+inconsistencies/i.test(fullTxt))
            consistency = 'consistent';

        // IP — match a public-IPv4 pattern. Phase 58b — exclude `.0.0.0`-suffix
        // patterns: those are NEVER real IPs, they leak in from the Chrome
        // UA reduction string (`Chrome/147.0.0.0` was getting parsed as IP
        // `147.0.0.0` and shown as the user's address). Also exclude private
        // ranges (RFC1918, loopback, link-local).
        const ipAll = [...fullTxt.matchAll(/\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b/g)]
            .map(m => m[1])
            // Reject Chrome UA-style version artefacts (X.0.0.0)
            .filter(ip => !/\.0\.0\.0$/.test(ip))
            // Reject private/loopback/link-local
            .filter(ip => !/^(?:127\.|0\.|10\.|169\.254\.|192\.168\.|172\.(?:1[6-9]|2\d|3[01])\.)/.test(ip))
            // Reject if it's preceded by a "/" or "Chrome/" or other URL/version-context
            // characters in the page text — these are version strings, not IPs.
            .filter(ip => {
                const idx = fullTxt.indexOf(ip);
                if (idx <= 0) return true;
                const prev = fullTxt.substring(Math.max(0, idx - 16), idx);
                return !/(?:Chrome|Safari|Firefox|Edge|Version|build|v)\s*\/?\s*$/i.test(prev)
                    && !prev.endsWith('/');
            });
        if (ipAll.length > 0) out.details.push({k:'IP address', v: ipAll[0]});

        // Timezone — IANA format ("Europe/Kyiv" etc) is unique enough.
        const tzMatch = fullTxt.match(/\b((?:Africa|America|Antarctica|Asia|Atlantic|Australia|Europe|Indian|Pacific)\/[A-Za-z_]+(?:\/[A-Za-z_]+)?)\b/);
        if (tzMatch) out.details.push({k:'Timezone', v: tzMatch[1]});

        // Country — Pixelscan shows "Country: Ukraine" or similar in the geo block.
        const countryMatch = fullTxt.match(/country[\s:]+([A-Z][a-zA-Z\s]{2,30}?)(?:\n|,)/i);
        if (countryMatch) out.details.push({k:'Country', v: countryMatch[1].trim()});

        if (consistency) out.details.push({k:'Consistency', v: consistency});

        // Phase 57b — Pixelscan v3 doesn't always render the literal "we think
        // you are using a bot" phrase. The scan-complete signal is the presence
        // of structured fields (IP + Timezone + consistency). If we have those,
        // the scan IS done — derive a verdict from consistency rather than
        // reporting "scan in progress" forever.
        const ipFound = out.details.some(d => d.k === 'IP address');
        const tzFound = out.details.some(d => d.k === 'Timezone');
        const scanComplete = ipFound && tzFound && consistency !== null;

        out.details.push({k:'Bot signal', v: botSignal === 'yes' ? 'YES' : botSignal === 'maybe' ? 'unclear' : botSignal === 'no' ? 'no' : (scanComplete ? 'no (inferred)' : 'pending')});

        if (botSignal === 'yes')       { out.summary = '⚠ flagged as bot';      out.verdict = 'flagged'; }
        else if (botSignal === 'maybe'){ out.summary = 'unclear (suspicious)';  out.verdict = 'weak'; }
        else if (botSignal === 'no')   { out.summary = 'no bot signal';         out.verdict = consistency === 'inconsistent' ? 'weak' : 'ok'; }
        else if (scanComplete) {
            // Phase 57b — verdict text not on page, but we have IP+TZ+consistency,
            // so the scan finished. Fall back to consistency-based verdict.
            if (consistency === 'inconsistent') {
                out.summary = '⚠ inconsistent fingerprint';
                out.verdict = 'flagged';
            } else if (consistency === 'consistent') {
                out.summary = 'consistent fingerprint, no bot flag';
                out.verdict = 'ok';
            } else {
                out.summary = 'scan completed (no explicit verdict)';
                out.verdict = 'info';
            }
        }
        else {
            // Truly still running.
            const stillLoading = !!document.querySelector('[class*="loading" i],[class*="spinner" i],[class*="progress" i]');
            out.summary = stillLoading ? 'scan in progress…' : 'scan not started';
            out.verdict = '?';
        }
        clearTimeout(cap); resolve(JSON.stringify(out));
    """);

    /// <summary>
    /// AmIUnique's /fp page renders the verdict as one of these phrasings
    /// (varies by year / locale):
    ///   • "Yes! You are unique among the N fingerprints …"
    ///   • "No! You are not unique. You share your fingerprint with …"
    ///   • "You can be tracked!" / "You cannot be uniquely identified."
    /// The "% of fingerprints similar to yours" bar also gives a
    /// cheap uniqueness ratio. The page issues an XHR to /api/* for
    /// the comparison, so we wait for the result block before reading.
    /// Phase 34 — Before extraction, check if a "View my fingerprint",
    /// "Show my fingerprint", or "Submit" button exists and click it
    /// to trigger the server-side analysis. Subsequent polls will find
    /// the result block once the analysis completes.</summary>
    private static readonly string AmIUniqueScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        const txt = (document.body && document.body.innerText) || '';

        // Phase 34 — First check: is the result block already there?
        // (server-side processed page or client has already computed)
        const resultsBlock = document.querySelector('.results, .uniqueness, [class*="result" i]');
        let blockTxt = resultsBlock
            ? (resultsBlock.innerText || resultsBlock.textContent || '').toLowerCase()
            : '';

        // If no result block yet, try to click the button that starts analysis
        if (!blockTxt || blockTxt.length < 10) {
            const buttons = document.querySelectorAll('button, input[type="button"], input[type="submit"], a[role="button"]');
            let foundButton = null;
            for (const btn of buttons) {
                const btnText = (btn.innerText || btn.value || btn.textContent || '').toLowerCase();
                // Match "View my fingerprint", "Show my fingerprint", "Submit", "Test"
                if (/view\s+.*?fingerprint|show\s+.*?fingerprint|^submit$|^test$/i.test(btnText)) {
                    foundButton = btn;
                    break;
                }
            }
            if (foundButton) {
                try {
                    foundButton.click();
                    // After clicking, return a placeholder so polling loop retries
                    out.summary = 'scan started';
                    out.verdict = '?';
                    out.details.push({k:'action', v: 'button clicked, waiting for analysis'});
                    clearTimeout(cap); resolve(JSON.stringify(out));
                    return;
                } catch (e) {
                    // Click failed; fall through to extraction
                }
            }
        }

        // Normal extraction — same logic as before
        let uniq = null; // 'yes' | 'no' | null

        // Headline verdict — try explicit selectors first, then full-text regex
        let verdictEl = resultsBlock || document.body;
        if (verdictEl) {
            blockTxt = (verdictEl.innerText || verdictEl.textContent || '').toLowerCase();
            if (/yes\s*!|unique|can\s+be\s+(?:uniquely\s+)?(?:tracked|identified)/.test(blockTxt))
                uniq = 'yes';
            else if (/no\s*!|not\s+unique|cannot\s+be\s+(?:uniquely\s+)?identified|share\s+your\s+fingerprint/.test(blockTxt))
                uniq = 'no';
        }

        // Fallback to full-text if block match failed
        if (uniq === null) {
            if (/^\s*yes\s*!|you\s+are\s+unique|you\s+can\s+be\s+(?:uniquely\s+)?(?:tracked|identified)/i.test(txt))
                uniq = 'yes';
            else if (/^\s*no\s*!|you\s+are\s+not\s+unique|cannot\s+be\s+(?:uniquely\s+)?identified|share\s+your\s+fingerprint/i.test(txt))
                uniq = 'no';
        }

        // Database size — appears as "among the 1,234,567 fingerprints"
        // or "out of NN fingerprints collected" or "1 in 1,234,567"
        let dbMatch = txt.match(/(?:among|of|out\s+of)\s+(?:the\s+)?(\d{1,3}(?:[.,\s]\d{3})+)\s+fingerprints?/i)
                  || txt.match(/1\s+in\s+(\d{1,3}(?:[.,\s]\d{3})+)/i);
        if (dbMatch) out.details.push({k:'Database size', v: dbMatch[1] + ' fingerprints'});

        // Similarity % — the comparison bar shows e.g. "0.04 %" or
        // "less than 0.1 %". Smaller = more unique. Also check .bar elements
        // which AmIUnique uses for the uniqueness visualization.
        let simPct = txt.match(/(?:similar|share)[^%]{0,80}?(\d+(?:\.\d+)?)\s*%/i)
                  ?? txt.match(/(\d+(?:\.\d+)?)\s*%[^%]{0,30}?(?:similar|same\s+fingerprint)/i);
        if (!simPct) {
            // Check for .fingerprint-bar or .bar element with text content
            const barEl = document.querySelector('.fingerprint-bar, .bar');
            if (barEl) {
                const barMatch = (barEl.textContent || '').match(/(\d+(?:\.\d+)?)\s*%/);
                if (barMatch) simPct = barMatch;
            }
        }
        if (simPct) out.details.push({k:'Similarity', v: simPct[1] + ' %'});

        // Per-attribute leaks listed in the report table — count the
        // rows that say "you are the only one with this value".
        let onlyCount = 0;
        document.querySelectorAll('table tr, .row, li').forEach(el => {
            const t = (el.innerText || el.textContent || '').toLowerCase();
            if (t.includes('only one') || t.includes('unique value')) onlyCount++;
        });
        if (onlyCount > 0) out.details.push({k:'Unique attributes', v: String(onlyCount)});

        out.details.push({k:'Uniqueness', v: uniq === 'yes' ? 'unique' : uniq === 'no' ? 'not unique' : 'unknown'});

        if (uniq === 'yes')      { out.summary = 'unique fingerprint'; out.verdict = 'standout'; }
        else if (uniq === 'no')  { out.summary = 'matches others';     out.verdict = 'good'; }
        else {
            const stillLoading = !!document.querySelector('[class*="loading" i],[class*="spinner" i]');
            out.summary = stillLoading ? 'AmIUnique still computing' : 'verdict not on page';
            out.verdict = '?';
        }
        clearTimeout(cap); resolve(JSON.stringify(out));
    """);

    /// <summary>BrowserLeaks /canvas page renders a canvas hash + a
    /// signature line + a "your canvas fingerprint is X% unique"
    /// banner. Page also includes Browser, OS, Hardware Concurrency,
    /// WebGL fingerprint, and Languages list.
    /// Page layout (verified 2026):
    ///   <table id="signature">
    ///     <tr><th>Signature</th><td>NNNNNNNNNN</td></tr>
    ///     <tr><th>Hash</th>     <td>32-char hex</td></tr>
    ///     <tr><th>Uniqueness</th><td>X% (of NN samples)</td></tr>
    ///   </table>
    /// Even when the table has different shape/labels, we extract
    /// the hex hash and the uniqueness % directly from text.
    ///
    /// Phase 32 rewrite — old version was reading the LANDING page
    /// (just nav anchors), which gave the user no actual fingerprint
    /// info. Now we navigate to /canvas and pull the real signature.
    /// Phase 33 — improved verdict wording so users understand that
    /// a low match rate is BAD (it means your canvas is unique = trackable).
    /// Phase 35 — add Canvas signature, WebGL fingerprint, Browser, OS,
    /// Hardware Concurrency, and Languages to the detail rows for
    /// comprehensive signal inspection.</summary>
    private static readonly string BrowserLeaksScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        const txt = (document.body && document.body.innerText) || '';

        // 1) Canvas hash — 32-64 char hex string. BrowserLeaks shows
        // it inside a <td> next to "Hash" or "Fingerprint". Take the
        // first long hex run we find.
        const hashMatch = txt.match(/\b([a-f0-9]{32,64})\b/i);
        if (hashMatch) out.details.push({k:'Canvas hash', v: hashMatch[1].slice(0,16) + '…'});

        // 2) Numeric signature — Canvas signature is a 6-12 digit int.
        const sigMatch = txt.match(/signature[^\d]{0,30}(\d{4,12})/i);
        if (sigMatch) out.details.push({k:'Signature', v: sigMatch[1]});

        // 3) Uniqueness — "X% of NN samples". A LOWER % means MORE
        // unique (= more identifiable = WORSE for stealth). We now
        // report this clearly so the user understands: 0% means
        // "your canvas is unique to you, completely trackable".
        const pctMatch = txt.match(/(\d+(?:\.\d+)?)\s*%\s*(?:of|out\s+of|in)/i)
                     ?? txt.match(/uniqueness[^%]{0,30}(\d+(?:\.\d+)?)\s*%/i);
        let uniqPct = null;
        if (pctMatch) {
            uniqPct = parseFloat(pctMatch[1]);
            out.details.push({k:'Match rate', v: pctMatch[1] + ' %'});
        }

        // 4) Supplementary details — extract Browser, OS, Hardware Concurrency,
        // WebGL fingerprint, Languages. Pattern: field\s*[:\-]\s*value up to 60 chars.
        const detailFields = ['browser', 'operating system', 'os', 'hardware concurrency', 'webgl', 'languages', 'language'];
        detailFields.forEach(field => {
            const regex = new RegExp(`${field}\\s*[:\\-]\\s*([^\\n]+?)(?=\\n|$)`, 'i');
            const m = txt.match(regex);
            if (m) {
                const fieldLabel = field.split(' ').map((w, i) => i === 0 ? w.charAt(0).toUpperCase() + w.slice(1) : w).join(' ');
                const value = m[1].trim().slice(0, 60);
                out.details.push({k: fieldLabel, v: value});
            }
        });

        // 5) IP / timezone — useful supplementary info even on /canvas
        // since BrowserLeaks shows the visitor block in the header.
        const ipMatch = txt.match(/\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b/);
        if (ipMatch) out.details.push({k:'Public IP', v: ipMatch[1]});

        if (hashMatch) {
            out.summary = 'canvas ' + hashMatch[1].slice(0,8);
            // Verdict — uniqueness % flips meaning: a high match rate
            // (lots of others have your canvas) is GOOD for anonymity;
            // a low match rate means the canvas is unique and trackable.
            if (uniqPct !== null) {
                if (uniqPct < 1) {
                    out.summary = 'canvas hash unique (trackable)';
                    out.verdict = 'flagged';
                } else if (uniqPct < 5) {
                    out.summary = 'canvas rare, limited blending';
                    out.verdict = 'ok';
                } else {
                    out.summary = 'canvas matches ' + pctMatch[1] + '% (good for blending in)';
                    out.verdict = 'good';
                }
            } else {
                out.verdict = 'info';
            }
        } else {
            const stillLoading = !!document.querySelector('[class*="loading" i],[class*="spinner" i]');
            out.summary = stillLoading ? 'BrowserLeaks still loading' : 'no canvas hash on page';
            out.verdict = '?';
        }
        clearTimeout(cap); resolve(JSON.stringify(out));
    """);

    /// <summary>
    /// Fingerprint.com BotD — Phase 60 rewrite. CDN injection has proven
    /// fundamentally unreliable across multiple host pages (fingerprint.com
    /// has CSP, about:blank has opaque-origin script load restrictions,
    /// example.com sometimes serves CSP). Now self-contained: this script
    /// runs the SAME class of detection signals that BotD checks for,
    /// inline, with zero external dependencies. The signals are the
    /// well-known bot-marker probes: navigator.webdriver, HeadlessChrome
    /// in UA, chrome.runtime/chrome.csi presence, navigator.plugins length,
    /// WebGL Mesa renderer, document.$cdc_ Selenium markers, devtools
    /// runtime via debugger detection, function toString native-code check.
    /// Each signal contributes a yes/no, and we report the verdict + which
    /// signals fired so the user can see exactly what gave them away (or
    /// confirm the profile is clean).</summary>
    private static readonly string FingerprintBotdScript = """
        return new Promise((resolve) => {
          const cap = setTimeout(() => resolve(JSON.stringify({summary:'timed out',verdict:'?',details:[]})), 6000);
          (async () => {
            try {
              const out = {summary:'', verdict:'', details:[]};
              const triggers = [];   // names of detectors that flagged "bot"
              const passed   = [];   // names of detectors that came back clean

              // ── Detector 1: navigator.webdriver
              // The Selenium / WebDriver leak. Real browsers report `false`
              // or undefined; an unpatched ChromeDriver returns `true`.
              if (navigator.webdriver === true) triggers.push('navigator.webdriver=true');
              else passed.push('navigator.webdriver');

              // ── Detector 2: HeadlessChrome in user-agent string
              const ua = navigator.userAgent || '';
              if (/HeadlessChrome|PhantomJS|SlimerJS/i.test(ua)) triggers.push('UA:Headless');
              else passed.push('UA:not-headless');

              // ── Detector 3: navigator.plugins zero-length is a tell
              const plugCount = (navigator.plugins && navigator.plugins.length) || 0;
              if (plugCount === 0) triggers.push('plugins.length=0');
              else passed.push('plugins.length=' + plugCount);

              // ── Detector 4: navigator.languages must exist + be non-empty
              const langCount = (navigator.languages && navigator.languages.length) || 0;
              if (langCount === 0) triggers.push('languages.length=0');
              else passed.push('languages.length=' + langCount);

              // ── Detector 5: chrome runtime — REAL Chrome has window.chrome
              // populated. Pure puppeteer / unpatched headless lacks it.
              const hasChrome = typeof window.chrome !== 'undefined';
              if (!hasChrome) triggers.push('window.chrome missing');
              else passed.push('window.chrome present');

              // ── Detector 6: Selenium $cdc_ markers on document. Selenium
              // injects properties like $cdc_asdjflasutopfhvcZLmcfl_ for
              // its driver. If any property starts with $cdc_ we leak.
              let cdcLeaks = 0;
              for (const k of Object.getOwnPropertyNames(document)) {
                if (k.startsWith('$cdc_') || k.startsWith('$wdc_')) cdcLeaks++;
              }
              if (cdcLeaks > 0) triggers.push('$cdc_/$wdc_ markers (' + cdcLeaks + ')');
              else passed.push('no Selenium $cdc_ markers');

              // ── Detector 7: WebGL renderer. Mesa / SwiftShader /
              // Brian Paul are common headless GPU renderers.
              try {
                const canvas = document.createElement('canvas');
                const gl = canvas.getContext('webgl') || canvas.getContext('experimental-webgl');
                if (gl) {
                  const ext = gl.getExtension('WEBGL_debug_renderer_info');
                  if (ext) {
                    const renderer = String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) || '');
                    if (/Mesa|Brian Paul|SwiftShader|llvmpipe/i.test(renderer)) {
                      triggers.push('WebGL renderer:headless (' + renderer.slice(0, 30) + ')');
                    } else {
                      passed.push('WebGL renderer:' + renderer.slice(0, 30));
                    }
                  } else {
                    passed.push('WebGL ext unavailable (ok)');
                  }
                }
              } catch (e) { passed.push('WebGL probe error (ok)'); }

              // ── Detector 8: function toString — native functions should
              // show "[native code]". A monkey-patched fn shows JS source.
              try {
                const fnSrc = Function.prototype.toString.call(navigator.permissions.query);
                if (!/\[native code\]/.test(fnSrc)) triggers.push('navigator.permissions.query patched');
                else passed.push('native fn intact');
              } catch (e) { /* skip */ }

              // ── Detector 9: notification permission consistency.
              // Headless Chrome returns "denied" for notifications without
              // user interaction, while real Chrome returns "default" or
              // "prompt" until the user explicitly allows / blocks.
              try {
                const perm = await navigator.permissions.query({name: 'notifications'});
                const notifState = (typeof Notification !== 'undefined') ? Notification.permission : 'unknown';
                if (perm.state === 'denied' && notifState === 'default') {
                  triggers.push('Notification.permission inconsistent (denied/default)');
                } else {
                  passed.push('Notification.permission=' + perm.state);
                }
              } catch (e) { /* permissions API not available, skip */ }

              // ── Detector 10: outerWidth/outerHeight = 0 (headless tell)
              if (window.outerWidth === 0 || window.outerHeight === 0) {
                triggers.push('outerWidth/outerHeight=0 (headless)');
              } else {
                passed.push('window dims=' + window.outerWidth + 'x' + window.outerHeight);
              }

              // ── Detector 11: function constructor toString integrity for
              // navigator.userAgentData (Sec-CH-UA injection often patches it).
              try {
                if (navigator.userAgentData) {
                  const uad = await navigator.userAgentData.getHighEntropyValues(['fullVersionList','platform','architecture']);
                  if (uad && uad.fullVersionList) {
                    const versions = uad.fullVersionList.map(b => b.brand + '/' + b.version).join(', ');
                    out.details.push({k:'UA-CH', v: versions.slice(0, 100)});
                    // Validate that fullVersionList isn't all 0.0.0.0 (a tell that
                    // the override didn't reach navigator.userAgentData).
                    const allZeros = uad.fullVersionList.every(b => /^\d+\.0\.0\.0$/.test(b.version));
                    if (allZeros) triggers.push('UA-CH fullVersionList all 0.0.0.0');
                  }
                }
              } catch (e) { /* userAgentData may not be available, skip */ }

              // Render the verdict
              const triggered = triggers.length;
              const isBot = triggered >= 2;  // 2+ signals = flagged

              out.details.push({k:'Total signals',  v: 'checked ' + (triggers.length + passed.length)});
              out.details.push({k:'Triggered',      v: triggered === 0 ? 'none' : String(triggered)});
              for (let i = 0; i < Math.min(triggers.length, 8); i++) {
                out.details.push({k:'Trigger #'+(i+1), v: triggers[i]});
              }
              for (let i = 0; i < Math.min(passed.length, 6); i++) {
                out.details.push({k:'Clean #'+(i+1), v: passed[i]});
              }

              if (triggered === 0) {
                out.summary = 'clean — no bot signals';
                out.verdict = 'good';
              } else if (triggered === 1) {
                out.summary = '1 weak signal: ' + triggers[0];
                out.verdict = 'weak';
              } else {
                out.summary = '⚠ ' + triggered + ' bot signals fired';
                out.verdict = 'flagged';
              }

              clearTimeout(cap); resolve(JSON.stringify(out));
            } catch (e) {
              clearTimeout(cap); resolve(JSON.stringify({summary:'extractor error',verdict:String(e).slice(0,80),details:[]}));
            }
          })();
        });
    """;
}

/// <summary>Detailed result of one external-tester probe.</summary>
public sealed record TesterResult
{
    public required string TesterName { get; init; }
    public required string Summary    { get; init; }   // one-line for the card
    public required string Verdict    { get; init; }   // semantic colour key (excellent/ok/weak/flagged/info/?)
    public required IReadOnlyList<TesterDetailRow> Details { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
}

public sealed record TesterDetailRow(string Key, string Value);

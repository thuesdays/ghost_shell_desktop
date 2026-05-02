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
    /// <summary>How long the page needs to finish its self-assessment
    /// after navigation. Real numbers gathered by watching dev-tools
    /// network panels on a fresh profile.
    ///
    /// Phase 32 — bumped CreepJS / Pixelscan / AmIUnique / BrowserLeaks
    /// after observing the previous values fired the extractor BEFORE
    /// the page had finished its async fingerprint dump. CreepJS runs
    /// canvas + audio + fonts probes serially and routinely takes 15-
    /// 18 s before the trust score widget paints. Pixelscan posts back
    /// to /v3/check after ~6 s; the verdict text only renders after the
    /// JSON returns. AmIUnique's /fingerprint page redirects to /fp/&lt;id&gt;
    /// once the server-side analysis completes. BrowserLeaks' /canvas
    /// page draws + hashes the canvas inline — usually fast but its
    /// "signature" line is filled in via XHR.</summary>
    public static TimeSpan SettleFor(string testerName) => testerName switch
    {
        "CreepJS"               => TimeSpan.FromSeconds(18),
        "BrowserLeaks"          => TimeSpan.FromSeconds(8),
        "Pixelscan"             => TimeSpan.FromSeconds(14),
        "Fingerprint.com BotD"  => TimeSpan.FromSeconds(6),
        "AmIUnique"             => TimeSpan.FromSeconds(12),
        "Sannysoft Bot Test"    => TimeSpan.FromSeconds(4),
        _                        => TimeSpan.FromSeconds(5),
    };

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
    /// the lies array length via DOM count.</summary>
    private static readonly string CreepJsScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        const txt = (document.body && document.body.innerText) || '';
        const html = (document.body && document.body.innerHTML) || '';

        // 1) Trust score — appears in many shapes. Try a sequence:
        //   • Element with class containing 'trust' showing "87%"
        //   • innerText with "trust score: 87%" or "87 / 100"
        //   • A bare "NN%" inside an .fp / .fingerprint container.
        let score = null;
        const trustEl = document.querySelector('[class*="trust" i], [class*="score" i]');
        if (trustEl) {
            const m = (trustEl.textContent || '').match(/(\d{1,3})\s*%/);
            if (m) score = parseInt(m[1], 10);
        }
        if (score === null) {
            const m = txt.match(/trust(?:\s*score)?[^\d]{0,20}(\d{1,3})\s*(?:%|\/\s*100)/i);
            if (m) score = parseInt(m[1], 10);
        }
        if (score === null) {
            // Last-ditch — first NN% in the page that isn't 100% or 0%.
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

    /// <summary>Sannysoft renders a table of pass/fail rows. We count
    /// passing rows (background-colour green) vs failing (red) by
    /// reading the inline style each row's td uses.</summary>
    private static readonly string SannysoftScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        let pass = 0, fail = 0;
        document.querySelectorAll('table tr').forEach(tr => {
            const txt = (tr.innerText || '').toLowerCase();
            if (txt.includes('passed')) pass++;
            else if (txt.includes('failed')) fail++;
        });
        out.details.push({k:'Passed checks', v: String(pass)});
        out.details.push({k:'Failed checks', v: String(fail)});
        out.summary = pass + ' pass / ' + fail + ' fail';
        out.verdict = fail === 0 ? 'clean' : fail <= 1 ? 'mostly ok' : 'flagged';
        clearTimeout(cap); resolve(JSON.stringify(out));
    """);

    /// <summary>Pixelscan v3 exposes the verdict as one of three
    /// banner phrasings:
    ///   • "We don't think you are using a bot"          → ok
    ///   • "We are not sure if you are using a bot"      → weak
    ///   • "We think you are using a bot"                → flagged
    /// Plus a Consistency row ("Consistent"/"Inconsistent") and an
    /// IP / location header. The result block only paints AFTER the
    /// browser POSTs to /v3/check and the JSON returns (~6-10 s after
    /// landing), so the outer settle waits 14 s before this runs.
    ///
    /// Phase 32 rewrite — old regex hit "consistent" inside a tooltip
    /// before the verdict block even rendered, and missed the "we
    /// don't think" / "we are not sure" phrasings entirely.</summary>
    private static readonly string PixelscanScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        const txt = (document.body && document.body.innerText) || '';

        // Bot verdict — most specific phrases first so "we think you
        // are using a bot" doesn't accidentally match the "we don't
        // think" branch.
        let botSignal = null;       // 'no' | 'maybe' | 'yes'
        if (/we\s+do\s*n[o']?t\s+think.*?bot/i.test(txt))      botSignal = 'no';
        else if (/we\s+are\s+not\s+sure.*?bot/i.test(txt))     botSignal = 'maybe';
        else if (/we\s+think.*?(?:are|is)\s+(?:using\s+)?a?\s*bot/i.test(txt)) botSignal = 'yes';
        else if (/bot\s+detected|likely\s+bot|automation\s+detected/i.test(txt)) botSignal = 'yes';
        else if (/no\s+bot\s+signal|not\s+a\s+bot/i.test(txt)) botSignal = 'no';

        // Consistency — Pixelscan calls this out in a labelled card.
        let consistency = null;     // 'consistent' | 'inconsistent' | null
        if (/inconsistent/i.test(txt))                            consistency = 'inconsistent';
        else if (/\bconsistent\b/i.test(txt) && !/inconsistent/i.test(txt)) consistency = 'consistent';

        // IP + location — extract for the detail dialog.
        const ipMatch = txt.match(/\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b/);
        if (ipMatch) out.details.push({k:'IP address', v: ipMatch[1]});
        const tzMatch = txt.match(/\b((?:Africa|America|Antarctica|Asia|Atlantic|Australia|Europe|Indian|Pacific)\/[A-Za-z_]+(?:\/[A-Za-z_]+)?)\b/);
        if (tzMatch) out.details.push({k:'Timezone', v: tzMatch[1]});

        out.details.push({k:'Consistency', v: consistency || 'unknown'});
        out.details.push({k:'Bot signal',  v: botSignal === 'yes' ? 'YES' : botSignal === 'maybe' ? 'unclear' : botSignal === 'no' ? 'no' : 'unknown'});

        if (botSignal === 'yes')      { out.summary = '⚠ flagged as bot';   out.verdict = 'flagged'; }
        else if (botSignal === 'maybe'){ out.summary = 'unclear';            out.verdict = 'weak'; }
        else if (botSignal === 'no')   { out.summary = 'no bot signal';      out.verdict = consistency === 'inconsistent' ? 'weak' : 'ok'; }
        else {
            // No verdict text yet — still scanning?
            const stillLoading = !!document.querySelector('[class*="loading" i],[class*="spinner" i],[class*="progress" i]');
            out.summary = stillLoading ? 'Pixelscan still scanning' : 'verdict not on page';
            out.verdict = '?';
        }
        clearTimeout(cap); resolve(JSON.stringify(out));
    """);

    /// <summary>AmIUnique's /fingerprint page renders the verdict as
    /// one of these phrasings (varies by year / locale):
    ///   • "Yes! You are unique among the N fingerprints …"
    ///   • "No! You are not unique. You share your fingerprint with …"
    ///   • "You can be tracked!" / "You cannot be uniquely identified."
    /// The "% of fingerprints similar to yours" bar also gives a
    /// cheap uniqueness ratio. The page issues an XHR to /api/* for
    /// the comparison, so we wait for the result block before reading.
    ///
    /// Phase 32 rewrite — earlier regex required the literal
    /// "your fingerprint is/appears to be unique" which AmIUnique
    /// doesn't actually use anymore (it leads with "Yes!" / "No!").
    /// We now match the headline yes/no and pull the comparison %.</summary>
    private static readonly string AmIUniqueScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        const txt = (document.body && document.body.innerText) || '';

        // Headline verdict.
        let uniq = null; // 'yes' | 'no' | null
        if (/^\s*yes\s*!|you\s+are\s+unique|you\s+can\s+be\s+(?:uniquely\s+)?(?:tracked|identified)/i.test(txt))
            uniq = 'yes';
        else if (/^\s*no\s*!|you\s+are\s+not\s+unique|cannot\s+be\s+(?:uniquely\s+)?identified|share\s+your\s+fingerprint/i.test(txt))
            uniq = 'no';

        // Database size — appears as "among the 1,234,567 fingerprints"
        // or "out of NN fingerprints collected".
        const dbSize = txt.match(/(?:among|of|out\s+of)\s+(?:the\s+)?(\d{1,3}(?:[.,\s]\d{3})+)\s+fingerprints?/i);
        if (dbSize) out.details.push({k:'Database size', v: dbSize[1] + ' fingerprints'});

        // Similarity % — the comparison bar shows e.g. "0.04 %" or
        // "less than 0.1 %". Smaller = more unique.
        const simPct = txt.match(/(?:similar|share)[^%]{0,80}?(\d+(?:\.\d+)?)\s*%/i)
                   ?? txt.match(/(\d+(?:\.\d+)?)\s*%[^%]{0,30}?(?:similar|same\s+fingerprint)/i);
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
    /// banner.
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
    /// info. Now we navigate to /canvas and pull the real signature.</summary>
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
        // unique (= more identifiable = WORSE for stealth).
        const pctMatch = txt.match(/(\d+(?:\.\d+)?)\s*%\s*(?:of|out\s+of|in)/i)
                     ?? txt.match(/uniqueness[^%]{0,30}(\d+(?:\.\d+)?)\s*%/i);
        let uniqPct = null;
        if (pctMatch) {
            uniqPct = parseFloat(pctMatch[1]);
            out.details.push({k:'Match rate', v: pctMatch[1] + ' %'});
        }

        // 4) IP / timezone — useful supplementary info even on /canvas
        // since BrowserLeaks shows the visitor block in the header.
        const ipMatch = txt.match(/\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b/);
        if (ipMatch) out.details.push({k:'Public IP', v: ipMatch[1]});

        if (hashMatch) {
            out.summary = 'canvas ' + hashMatch[1].slice(0,8);
            // Verdict — uniqueness % flips meaning: a 100% match rate
            // (lots of others have your fingerprint) is GOOD for
            // anonymity; a < 1% match rate means the canvas is rare
            // and you're trackable.
            if (uniqPct !== null) {
                out.verdict = uniqPct >= 5 ? 'good' : uniqPct >= 1 ? 'ok' : 'flagged';
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

    /// <summary>Fingerprint.com BotD demo loads their botd library
    /// and writes the verdict into a #result element. We poll for
    /// that element; if the page's just-marketing version doesn't
    /// expose it, we fall back to scraping headline text.</summary>
    private static readonly string FingerprintBotdScript = Wrap("""
        const out = {summary:'', verdict:'', details:[]};
        const txt = document.body.innerText || '';
        const isBot = /(bot|automated)\s*detected/i.test(txt);
        const isHuman = /not\s*a\s*bot|human\s*detected|legitimate/i.test(txt);
        out.details.push({k:'BotD verdict', v: isBot ? 'BOT' : (isHuman ? 'HUMAN' : 'unknown')});
        out.summary = isBot ? '⚠ flagged as bot' : (isHuman ? 'detected as human' : 'BotD loaded');
        out.verdict = isBot ? 'flagged' : (isHuman ? 'good' : '?');
        clearTimeout(cap); resolve(JSON.stringify(out));
    """);
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

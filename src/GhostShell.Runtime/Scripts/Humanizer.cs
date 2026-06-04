// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Services;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Humanises browser interactions. Variable timings, never
/// deterministic. Same shape as the legacy "humanizer" layer that
/// drives mouse / keyboard / scroll patterns.
///
/// Real cursor curves require Windows SendInput which we don't ship
/// in v1 (clicks dispatch via JS mousedown/mouseup events). When
/// the SendInput path lands, this class is the integration point —
/// keep all timing knobs here.
/// </summary>
public static class Humanizer
{
    /// <summary>
    /// Type a string char-by-char with per-keystroke jitter.
    ///
    /// Stale-element protection (Phase 14 audit): instead of
    /// trusting <c>document.activeElement</c> on every char (a
    /// navigation between focus and the next char would type into
    /// whatever the new page's active element is — usually the
    /// body, frequently into the URL bar's autocomplete), we look
    /// the selector up FRESH on every keystroke and stamp a
    /// per-char data attribute so we know we're hitting the same
    /// element each time. If the original element vanishes
    /// (navigated away, replaced by SPA re-render), the next char
    /// throws — caller decides recovery.
    /// </summary>
    public static Task TypeAsync(
        IBrowserSession session, string selector, string text,
        int minMs = 40, int maxMs = 180, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return Task.CompletedTask;
        // audit SCRIPTSUPPORT-01: route typing through the session's TRUSTED
        // input path. On the Selenium session this uses WebDriver SendKeys,
        // which fires real keydown/keypress/input/keyup with isTrusted=true
        // (the old code wrote el.value+=c and dispatched a synthetic
        // InputEvent — isTrusted=false, and it fired NO keyboard events at
        // all, a strong bot signature). Per-key jitter (variable, not
        // uniform) lives in the session impl. Element-staleness across SPA
        // re-renders is handled there too (re-find on each keystroke).
        return session.TrustedTypeAsync(selector, text, minMs, maxMs, ct);
    }

    /// <summary>
    /// Click a selector with a brief hover-then-click cadence (real
    /// users don't click instantly on hover). For now both phases
    /// dispatch via JS — the actual MouseEvent sequence (mouseover →
    /// mousedown → mouseup → click) gives most page handlers what
    /// they're listening for.
    /// </summary>
    public static async Task ClickAsync(
        IBrowserSession session, string selector,
        int hoverMinMs = 200, int hoverMaxMs = 600, CancellationToken ct = default)
    {
        // Phase 66 — wait-for-selector before clicking. Pages (especially
        // Google SERPs and SPA results) often haven't finished rendering
        // by the time a recorded script reaches the click step. The old
        // path threw "selector not found" the moment the first
        // querySelector returned null, even though the element would
        // appear 500ms later. Now we poll up to 5s before giving up,
        // which dramatically improves replay success rate on dynamic
        // pages without slowing down the happy path (the loop exits the
        // moment the element is ready).
        const int waitTimeoutMs = 5000;
        const int pollIntervalMs = 200;
        var deadline = DateTime.UtcNow.AddMilliseconds(waitTimeoutMs);
        var foundJs = $$"""
            return !!document.querySelector({{JsonSerializer.Serialize(selector)}});
        """;
        var elementReady = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var present = await session.ExecuteScriptAsync(foundJs, null, ct);
            if (present is true) { elementReady = true; break; }
            await Task.Delay(pollIntervalMs, ct);
        }
        if (!elementReady)
            throw new InvalidOperationException(
                $"selector not found after {waitTimeoutMs}ms: {selector}");

        // audit SCRIPTRUNNER-01 / SCRIPTSUPPORT-08: hover then click through
        // the session's TRUSTED input path (CDP Input.dispatchMouseEvent on
        // the Selenium session) so the events carry isTrusted=true and a
        // real pointer trail (move → press → release), not a single
        // synthetic dispatchEvent at dead-centre. Keep the human
        // hover-then-click dwell between the two.
        await session.TrustedHoverAsync(selector, ct);
        await Task.Delay(Random.Shared.Next(hoverMinMs, hoverMaxMs + 1), ct);
        await session.TrustedClickAsync(selector, ct: ct);
    }

    /// <summary>
    /// Scroll progressively over <paramref name="totalSec"/> seconds.
    /// Each step nudges 200-700px and waits 800-2400ms. Caps at 12
    /// steps. Adds a small return-scroll at the end — looks like
    /// re-reading something the user noticed.
    /// </summary>
    public static async Task ScrollAsync(
        IBrowserSession session, double totalSec, CancellationToken ct = default)
    {
        var endsAt = DateTime.UtcNow.AddSeconds(Math.Max(1, totalSec));
        var step = 0;
        while (DateTime.UtcNow < endsAt && step < 12)
        {
            ct.ThrowIfCancellationRequested();
            var delta = Random.Shared.Next(200, 701);
            try
            {
                await session.ExecuteScriptAsync(
                    $"window.scrollBy({{top: {delta}, left: 0, behavior: 'smooth'}});",
                    null, ct);
            }
            catch { /* visit-non-fatal */ }
            await Task.Delay(Random.Shared.Next(800, 2401), ct);
            step++;
        }
        try
        {
            await session.ExecuteScriptAsync(
                "window.scrollBy({top: -120, left: 0, behavior: 'smooth'});",
                null, ct);
        }
        catch { }
    }

    /// <summary>
    /// Random "thinking" pause — used by move_random and similar
    /// idle-time fillers. Min ms, max ms; clamped to non-negative.
    /// </summary>
    public static Task IdleAsync(int minMs, int maxMs, CancellationToken ct = default)
    {
        if (minMs < 0) minMs = 0;
        if (maxMs < minMs) maxMs = minMs;
        var gap = Random.Shared.Next(minMs, maxMs + 1);
        return Task.Delay(gap, ct);
    }
}

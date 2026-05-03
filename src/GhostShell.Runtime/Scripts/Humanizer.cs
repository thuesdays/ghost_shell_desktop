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
    public static async Task TypeAsync(
        IBrowserSession session, string selector, string text,
        int minMs = 40, int maxMs = 180, CancellationToken ct = default)
    {
        if (text.Length == 0) return;
        // Focus + clear up front. Stamp the element with a unique
        // marker so we can detect staleness on each subsequent
        // keystroke without re-running the user's selector (which
        // might match a sibling element under SPA re-render).
        var marker = "gs-type-" + Guid.NewGuid().ToString("N")[..12];
        var focusJs = $$"""
            (function() {
              var el = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!el) return false;
              el.focus();
              if ('value' in el) el.value = '';
              else el.textContent = '';
              el.setAttribute('data-gs-typing', {{JsonSerializer.Serialize(marker)}});
              return true;
            })()
        """;
        var ok = await session.ExecuteScriptAsync(focusJs, null, ct);
        if (ok is not true)
            throw new InvalidOperationException($"selector not found: {selector}");

        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            var charJs = $$"""
                (function() {
                  var el = document.querySelector(
                    '[data-gs-typing="' + {{JsonSerializer.Serialize(marker)}} + '"]');
                  if (!el) return false;          // element gone (navigation / re-render)
                  if (document.activeElement !== el) el.focus();
                  var c = {{JsonSerializer.Serialize(ch.ToString())}};
                  if ('value' in el) el.value += c;
                  else el.textContent += c;
                  el.dispatchEvent(new InputEvent('input', {bubbles: true, data: c}));
                  return true;
                })()
            """;
            var alive = await session.ExecuteScriptAsync(charJs, null, ct);
            if (alive is not true)
                throw new InvalidOperationException(
                    $"typing target disappeared mid-input (selector: {selector})");
            // Keystroke gap. Random.Shared is process-wide thread-safe.
            var gap = Random.Shared.Next(minMs, maxMs + 1);
            await Task.Delay(gap, ct);
        }

        // Cleanup: drop the marker so we leave the DOM in the same
        // shape as a normal user-typed input.
        try
        {
            await session.ExecuteScriptAsync($$"""
                (function() {
                  var el = document.querySelector(
                    '[data-gs-typing="' + {{JsonSerializer.Serialize(marker)}} + '"]');
                  if (el) el.removeAttribute('data-gs-typing');
                })()
            """, null, ct);
        }
        catch { /* best-effort */ }
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

        var hoverJs = $$"""
            return (function() {
              var el = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!el) return false;
              // Scroll the element into view so the click hits a real
              // viewport coordinate (off-screen elements have rect
              // values outside the visible area).
              try { el.scrollIntoView({block: 'center', inline: 'center', behavior: 'instant'}); } catch (e) {}
              var rect = el.getBoundingClientRect();
              var cx = rect.left + rect.width / 2;
              var cy = rect.top + rect.height / 2;
              var ev = new MouseEvent('mouseover',
                {bubbles: true, cancelable: true, clientX: cx, clientY: cy});
              el.dispatchEvent(ev);
              return true;
            })();
        """;
        var ok = await session.ExecuteScriptAsync(hoverJs, null, ct);
        if (ok is not true)
            throw new InvalidOperationException($"selector not found: {selector}");
        await Task.Delay(Random.Shared.Next(hoverMinMs, hoverMaxMs + 1), ct);

        var clickJs = $$"""
            (function() {
              var el = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!el) return false;
              var r = el.getBoundingClientRect();
              var cx = r.left + r.width / 2;
              var cy = r.top + r.height / 2;
              ['mousedown','mouseup','click'].forEach(function(name) {
                el.dispatchEvent(new MouseEvent(name,
                  {bubbles: true, cancelable: true, clientX: cx, clientY: cy, button: 0}));
              });
              return true;
            })()
        """;
        await session.ExecuteScriptAsync(clickJs, null, ct);
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

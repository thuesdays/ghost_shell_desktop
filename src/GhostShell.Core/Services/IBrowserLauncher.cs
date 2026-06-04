// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Launches a Chromium browser bound to a profile. The returned
/// <see cref="IBrowserSession"/> owns the WebDriver / process tree
/// and exposes only what consumers (action runner, UI status checks,
/// stop button) actually need.
///
/// Selenium-specific surface stays inside GhostShell.Runtime — Core
/// keeps a minimal, technology-agnostic shape so tests / future
/// alternatives (Playwright, raw CDP) can plug in without ripple.
/// </summary>
public interface IBrowserLauncher
{
    Task<IBrowserSession> LaunchAsync(Profile profile, CancellationToken ct = default);
}

/// <summary>
/// Active browser instance owned by IBrowserLauncher. Disposing it
/// quits the WebDriver and tears down the chrome.exe / chromedriver
/// process tree (orphan-safe).
///
/// The cookie / storage methods on this surface are the contract the
/// session-and-cookies feature (Phase 4.2) builds on. They route
/// through Selenium's CDP bridge under the hood — direct
/// <c>Network.getAllCookies</c> / <c>Network.setCookies</c> for cookies
/// (avoids the per-domain navigation cost of <c>driver.add_cookie</c>),
/// and per-origin JS execution for localStorage / sessionStorage
/// (matches legacy <c>session/manager.py</c> exactly).
/// </summary>
public interface IBrowserSession : IAsyncDisposable
{
    string ProfileName { get; }
    long RunId { get; }
    DateTime StartedAt { get; }

    /// <summary>True while WebDriver still answers commands.</summary>
    bool IsAlive { get; }

    /// <summary>Open a URL in the (single) tab.</summary>
    Task NavigateAsync(string url, CancellationToken ct = default);

    /// <summary>Read the current document title — cheap liveness probe.</summary>
    Task<string?> GetTitleAsync(CancellationToken ct = default);

    // ─── Cookie & storage I/O ───────────────────────────────────

    /// <summary>
    /// Read every cookie known to the browser. Uses CDP
    /// <c>Network.getAllCookies</c> so we get cookies for every
    /// domain the browser has visited, not just the current page.
    /// </summary>
    Task<IReadOnlyList<CookieEntry>> GetCookiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Replace cookies in bulk via CDP <c>Network.setCookies</c>.
    /// No navigation required — cookies are stamped into Chromium's
    /// cookie store directly. Existing cookies with the same
    /// (name, domain, path) are overwritten.
    /// </summary>
    Task SetCookiesAsync(IEnumerable<CookieEntry> cookies, CancellationToken ct = default);

    /// <summary>Delete every cookie in the browser store.</summary>
    Task ClearCookiesAsync(CancellationToken ct = default);

    /// <summary>
    /// Capture localStorage + sessionStorage from each of the supplied
    /// origins. The browser navigates briefly to each origin to read
    /// (storage is origin-scoped). Origins it can't reach (offline,
    /// blocked, malformed) are skipped silently.
    /// </summary>
    Task<IReadOnlyList<StorageEntry>> GetStorageAsync(
        IEnumerable<string> origins, CancellationToken ct = default);

    /// <summary>
    /// Inject localStorage / sessionStorage at each entry's origin
    /// via JS. Same per-origin navigation pattern; entries with
    /// non-HTTP origins are skipped.
    /// </summary>
    Task SetStorageAsync(
        IEnumerable<StorageEntry> entries, CancellationToken ct = default);

    /// <summary>
    /// Execute arbitrary JavaScript in the current page context. Used
    /// by the warmup engine for consent-banner detection / clicking
    /// and gentle scroll simulation — both of which need DOM access
    /// the IBrowserSession surface doesn't otherwise expose.
    ///
    /// Returns the script's return value (boxed); typical results are
    /// <see cref="bool"/>, <see cref="string"/>, <see cref="long"/>,
    /// or <c>null</c>. Exceptions in the JS are surfaced as
    /// <see cref="System.Exception"/>; callers in the warmup loop
    /// generally swallow them — a script failure on one site is not
    /// a reason to abort the run.
    /// </summary>
    /// <param name="script">JS source. The whole-script body, not a function expression.</param>
    /// <param name="args">Optional args bound to <c>arguments[0..n-1]</c> in the script.</param>
    Task<object?> ExecuteScriptAsync(
        string script, object[]? args = null, CancellationToken ct = default);

    /// <summary>
    /// Phase 68 — list every open window/tab handle that the driver
    /// is attached to. Includes regular tabs, extension popups
    /// (chrome-extension://...), DevTools, and detached child windows.
    /// Used by ScriptRecorder to drain its event queue from EVERY
    /// window so user actions inside an OKX/MetaMask popup are
    /// captured the same as actions on the main page.
    /// </summary>
    Task<IReadOnlyList<string>> GetWindowHandlesAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase 68 — get the handle of the currently-focused window.
    /// Pair with <see cref="SwitchToWindowAsync"/> + a finally block
    /// to restore focus after a multi-window operation so the user's
    /// active tab doesn't get yanked under their feet.
    /// </summary>
    Task<string> GetCurrentWindowHandleAsync(CancellationToken ct = default);

    /// <summary>
    /// Phase 68 — switch driver focus to <paramref name="handle"/>.
    /// Subsequent ExecuteScriptAsync / NavigateAsync calls target the
    /// new window. No-op if the handle no longer exists; caller
    /// should re-enumerate before retrying.
    /// </summary>
    Task SwitchToWindowAsync(string handle, CancellationToken ct = default);

    /// <summary>
    /// Capture a PNG screenshot of the current viewport via CDP
    /// <c>Page.captureScreenshot</c>. Writes the bytes to
    /// <paramref name="path"/> (parent dir created if missing).
    /// Returns the path on success.
    /// </summary>
    Task<string> CaptureScreenshotAsync(string path, CancellationToken ct = default);

    // ─── Trusted input (audit SCRIPTRUNNER-01 / SCRIPTSUPPORT-01) ───────
    //
    // Automation used to drive clicks/typing/hover/keypress with JS
    // `element.dispatchEvent(new MouseEvent(...))`, which produces
    // events with `isTrusted === false`. Real user input is always
    // `isTrusted === true`; the gap is a trivial, widely-deployed bot
    // signal (e.g. Cloudflare / reCAPTCHA score it heavily).
    //
    // These methods dispatch input through the browser's REAL input
    // pipeline so events are `isTrusted === true`. The DEFAULT
    // implementations below fall back to the old synthetic JS path so
    // non-Selenium sessions (test doubles) keep working; the Selenium
    // session overrides them with CDP Input.* (mouse) and the WebDriver
    // key pipeline (keyboard).

    /// <summary>Trusted click at the element's centre. button: left|right|middle.</summary>
    async Task TrustedClickAsync(string selector, int clickCount = 1, string button = "left", CancellationToken ct = default)
    {
        var evName = button == "right" ? "contextmenu" : "click";
        var btn = button == "right" ? 2 : button == "middle" ? 1 : 0;
        var js = $$"""
            (function() {
              var el = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!el) return false;
              try { el.scrollIntoView({block:'center', inline:'center'}); } catch (e) {}
              var r = el.getBoundingClientRect();
              var cx = r.left + r.width/2, cy = r.top + r.height/2;
              var n = {{clickCount}};
              for (var i = 0; i < n; i++) {
                ['mousedown','mouseup','{{evName}}'].forEach(function(t){
                  el.dispatchEvent(new MouseEvent(t,{bubbles:true,cancelable:true,clientX:cx,clientY:cy,button:{{btn}}}));
                });
              }
              return true;
            })()
        """;
        var ok = await ExecuteScriptAsync(js, null, ct);
        if (ok is not true) throw new InvalidOperationException($"selector not found: {selector}");
    }

    /// <summary>Trusted hover (mouse move) over the element's centre.</summary>
    Task TrustedHoverAsync(string selector, CancellationToken ct = default)
    {
        var js = $$"""
            (function() {
              var el = document.querySelector({{JsonSerializer.Serialize(selector)}});
              if (!el) return false;
              var r = el.getBoundingClientRect();
              el.dispatchEvent(new MouseEvent('mouseover',{bubbles:true,clientX:r.left+r.width/2,clientY:r.top+r.height/2}));
              return true;
            })()
        """;
        return ExecuteScriptAsync(js, null, ct);
    }

    /// <summary>Focus the selector and type <paramref name="text"/> char-by-char with per-key jitter.</summary>
    async Task TrustedTypeAsync(string selector, string text, int minMs = 40, int maxMs = 180, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (maxMs < minMs) maxMs = minMs;
        var focus = $$"""
            (function(){var el=document.querySelector({{JsonSerializer.Serialize(selector)}});
              if(!el)return false; el.focus(); if('value' in el) el.value=''; else el.textContent=''; return true;})()
        """;
        if (await ExecuteScriptAsync(focus, null, ct) is not true)
            throw new InvalidOperationException($"selector not found: {selector}");
        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            var charJs = $$"""
                (function(){var el=document.querySelector({{JsonSerializer.Serialize(selector)}});
                  if(!el)return false; var c={{JsonSerializer.Serialize(ch.ToString())}};
                  if('value' in el) el.value+=c; else el.textContent+=c;
                  el.dispatchEvent(new InputEvent('input',{bubbles:true,data:c})); return true;})()
            """;
            await ExecuteScriptAsync(charJs, null, ct);
            await Task.Delay(Random.Shared.Next(minMs, maxMs + 1), ct);
        }
    }

    /// <summary>Press a single key (Enter, Tab, Escape, Arrow*, or a literal char) against the focused element.</summary>
    Task TrustedPressKeyAsync(string key, CancellationToken ct = default)
    {
        var js = $$"""
            (function(){var k={{JsonSerializer.Serialize(key)}};
              var t=document.activeElement||document.body;
              ['keydown','keyup'].forEach(function(n){t.dispatchEvent(new KeyboardEvent(n,{key:k,bubbles:true}));});
              return true;})()
        """;
        return ExecuteScriptAsync(js, null, ct);
    }
}

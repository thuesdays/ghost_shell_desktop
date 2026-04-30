// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

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
    /// Capture a PNG screenshot of the current viewport via CDP
    /// <c>Page.captureScreenshot</c>. Writes the bytes to
    /// <paramref name="path"/> (parent dir created if missing).
    /// Returns the path on success.
    /// </summary>
    Task<string> CaptureScreenshotAsync(string path, CancellationToken ct = default);
}

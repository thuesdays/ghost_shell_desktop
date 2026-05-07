// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Owns the lifecycle of running browser instances. Phase 2 ships a
/// stub that records "I would have launched X" in the log + writes a
/// run row to the DB so the UI shows the activity. Phase 3 swaps in
/// the real BrowserLauncher (patched Chromium + Selenium / CDP).
///
/// State changes are observable through <see cref="ActiveChanged"/>;
/// the proxy/profile pages subscribe to refresh "running" badges.
/// </summary>
public interface IProfileRunner
{
    /// <summary>True if at least one launch slot is currently active.</summary>
    bool HasActiveRuns { get; }

    /// <summary>Profile names that are currently in a "running" state.</summary>
    IReadOnlySet<string> ActiveProfileNames { get; }

    event EventHandler? ActiveChanged;

    /// <summary>
    /// Spawn a browser bound to the given profile. Returns the new
    /// run id (matches the row inserted into <c>runs</c>). Throws
    /// <see cref="InvalidOperationException"/> if the profile is
    /// already running.
    ///
    /// <para>When <paramref name="runAssignedScript"/> is true (default)
    /// the profile's assigned/default script kicks off automatically
    /// after the session settles — the standard "Run" button flow.
    /// Set it to <c>false</c> for "browser-only" launches such as the
    /// Fingerprint page's "Probe in profile" command, where we want
    /// a clean session for tester URLs and the user's GoodMedika /
    /// search-and-click script must NOT side-effect the probe results
    /// (the probe just lost a quarter of its tab budget to ad
    /// navigations and the user's CTR analytics show fake clicks).</para>
    ///
    /// <para>When <paramref name="restoreSession"/> is true (default)
    /// the latest saved session snapshot is auto-restored — cookies
    /// + per-origin localStorage. This can take 30+ seconds for fat
    /// snapshots (200 cookies, 20 storage origins each requiring a
    /// dedicated navigation to set its localStorage). Set false for
    /// flows that don't care about state — Fingerprint probes grade
    /// canvas/audio/WebGL signals which are state-independent, so
    /// skipping restore cuts probe latency from ~60s to ~5s.</para>
    /// </summary>
    Task<long> StartAsync(
        Profile profile, CancellationToken ct = default,
        bool runAssignedScript = true,
        bool restoreSession = true);

    /// <summary>
    /// Stop any in-flight run for the named profile. No-op if it
    /// wasn't running. Returns true on a clean stop.
    /// </summary>
    Task<bool> StopAsync(string profileName, CancellationToken ct = default);

    /// <summary>Stop everything. Used by "Stop all" / shutdown.</summary>
    Task StopAllAsync(CancellationToken ct = default);

    /// <summary>Phase 29 — return the live browser session for the
    /// named profile if one is running, otherwise null. Lets callers
    /// (Fingerprint page's external-tester probe) drive the same
    /// browser without spawning another. Implementation should NEVER
    /// expose the session if the runner is mid-teardown.</summary>
    IBrowserSession? TryGetActiveSession(string profileName);

    /// <summary>
    /// Phase 71ii — flag a profile so its NEXT launch skips the
    /// snapshot auto-restore. Used by the captcha-recovery cycle:
    /// when ScriptRunner detects a Google captcha (sorry/index
    /// redirect, recaptcha iframe, "unusual traffic" body) it
    /// reasonably concludes the saved cookies are poisoned and
    /// flags the profile so the next launch starts with fresh
    /// cookies. Flag is consumed (cleared) by the next StartAsync
    /// for that profile name; subsequent launches restore as
    /// usual unless the flag is set again. Idempotent — multiple
    /// calls collapse to one.
    /// </summary>
    void MarkSkipRestoreOnce(string profileName);
}

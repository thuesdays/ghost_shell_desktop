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
    /// </summary>
    Task<long> StartAsync(Profile profile, CancellationToken ct = default);

    /// <summary>
    /// Stop any in-flight run for the named profile. No-op if it
    /// wasn't running. Returns true on a clean stop.
    /// </summary>
    Task<bool> StopAsync(string profileName, CancellationToken ct = default);

    /// <summary>Stop everything. Used by "Stop all" / shutdown.</summary>
    Task StopAllAsync(CancellationToken ct = default);
}

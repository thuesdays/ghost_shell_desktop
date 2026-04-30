// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Persistence layer for <c>warmup_runs</c>. Split from the public
/// <see cref="IWarmupService"/> because the engine-orchestration class
/// lives in GhostShell.Runtime (it depends on IBrowserLauncher) but
/// the SQL layer wants the shared DatabaseConnection in
/// GhostShell.Data — keeping interface in Core lets both layers see it
/// without forcing Data to reference Runtime.
/// </summary>
public interface IWarmupHistoryService
{
    /// <summary>
    /// Insert a fresh row with status='running'. Returns the new id —
    /// the engine uses it as the in-memory handle until finish.
    /// </summary>
    Task<long> StartAsync(
        string profileName, string presetId, int sitesPlanned, string trigger,
        CancellationToken ct = default);

    /// <summary>
    /// Update the row at <see cref="StartAsync"/>'s id with terminal
    /// state. <paramref name="sitesLogJson"/> is the serialised
    /// <see cref="WarmupSiteResult"/> array.
    /// </summary>
    Task FinishAsync(
        long warmupId, string status,
        int sitesVisited, int sitesSucceeded, double durationSec,
        string? notes, string sitesLogJson,
        CancellationToken ct = default);

    /// <summary>
    /// Best-effort cleanup for orphan rows — runs that started but
    /// never finished because the app crashed. Marks them as
    /// <c>failed</c> with note "abandoned (app exited)". Called once
    /// at startup by the warmup service.
    /// </summary>
    Task<int> SweepOrphansAsync(CancellationToken ct = default);

    /// <summary>Recent rows for one profile (or all profiles if null).</summary>
    Task<IReadOnlyList<WarmupRun>> ListAsync(
        string? profileName = null, int limit = 50, CancellationToken ct = default);

    /// <summary>Latest row for the profile, or null if it's never been warmed up.</summary>
    Task<WarmupRun?> GetLatestAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// True if the profile currently has a <c>status='running'</c> row.
    /// Used by the engine for re-entry guard.
    /// </summary>
    Task<bool> IsRunningAsync(string profileName, CancellationToken ct = default);
}

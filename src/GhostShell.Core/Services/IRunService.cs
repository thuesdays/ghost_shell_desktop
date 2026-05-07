// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Read + write API for run history. Phase 4 adds Start/Finish so
/// the runner can stamp the DB with real run rows instead of the
/// synthetic in-memory IDs Phase 3 used.
/// </summary>
public interface IRunService
{
    /// <summary>
    /// Recent runs, newest first. Filters mirror the legacy /api/runs
    /// endpoint so we can reuse the runs page UX one-to-one.
    /// </summary>
    Task<IReadOnlyList<Run>> ListAsync(
        int limit = 50,
        string? profileName = null,
        RunStatusFilter status = RunStatusFilter.All,
        int? sinceHours = null,
        CancellationToken ct = default);

    Task<Run?> GetAsync(long runId, CancellationToken ct = default);

    Task<RunStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    /// Insert a fresh row into <c>runs</c> with started_at = now,
    /// finished_at = NULL, exit_code = NULL ("running"). Returns the
    /// newly-assigned <c>id</c> the caller stamps onto the
    /// <see cref="IBrowserSession"/>. Idempotency is the caller's
    /// problem — every Start should produce a new row.
    /// </summary>
    Task<long> StartAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Mark a run finished. <paramref name="exitCode"/> = 0 → success;
    /// non-zero → failed; non-null <paramref name="lastError"/> is
    /// only stored on failure paths. <paramref name="stopReason"/>
    /// is a short tag describing HOW the run ended (clean,
    /// external_close, crash, etc); see <see cref="Run.StopReason"/>
    /// for the canonical value list. Best-effort: if the row is
    /// already terminated, this is a no-op.
    /// </summary>
    Task FinishAsync(
        long runId, int exitCode,
        string? lastError = null, string? stopReason = null,
        CancellationToken ct = default);

    /// <summary>
    /// Bump <c>heartbeat_at = now</c> for a still-running row. The
    /// watchdog calls this every 30s. Cheap UPDATE on the indexed
    /// PK, safe to call from a background loop.
    /// </summary>
    Task HeartbeatAsync(long runId, CancellationToken ct = default);

    /// <summary>
    /// User-driven "Mark failed" action — for runs that look stuck
    /// (no finished_at + no exit_code + heartbeat stale) the user
    /// can manually finalise them so the row stops blocking the
    /// "is this profile running?" check. Sets exit_code = -99 and
    /// stop_reason = "user_marked_failed".
    /// </summary>
    Task MarkFailedAsync(long runId, CancellationToken ct = default);

    /// <summary>
    /// Delete finished runs older than the cutoff. Pass null to
    /// nuke ALL finished runs. Active (still-running) rows are
    /// never touched. Returns rows deleted.
    /// </summary>
    Task<int> ClearAsync(DateTime? olderThan, CancellationToken ct = default);

    /// <summary>
    /// Phase 53 — delete a single run by ID. Used when the user
    /// clicks the trash icon on a runs-table row. Only deletes if
    /// the run has finished (exit_code is not NULL). Returns true
    /// if deleted; false if the run is still running.
    /// </summary>
    Task<bool> DeleteAsync(long runId, CancellationToken ct = default);

    /// <summary>
    /// Phase 71dd — stamp the per-run counters (queries / ads /
    /// captchas) onto the row. The script runner accumulates these
    /// internally and calls this once at the end of ExecuteAsync.
    /// Pre-fix the runs.total_queries / total_ads / captchas columns
    /// were created in V1 but nobody ever UPDATEd them, so the Run
    /// History grid showed REQ=ADS=CAPTCHA=0 across the board even
    /// while ads were genuinely being clicked.
    /// </summary>
    Task UpdateCountersAsync(
        long runId, int totalQueries, int totalAds, int captchas,
        CancellationToken ct = default);
}

public enum RunStatusFilter
{
    All,
    Successful,
    Failed,
    Running,
}

/// <summary>
/// Aggregated counters used on Overview / Runs.
///
/// Declared as a record with init-only properties (NOT positional)
/// so Dapper can do name-based mapping with type conversion. SQLite
/// returns COUNT/SUM as Int64; a positional ctor of (int, int, ...)
/// would force Dapper to look for a literal (long, long, long, long)
/// constructor signature and throw at materialization time. With
/// init-properties Dapper just calls each setter and converts.
/// </summary>
public sealed record RunStats
{
    public int Total      { get; init; }
    public int Successful { get; init; }
    public int Failed     { get; init; }
    public int Running    { get; init; }
}

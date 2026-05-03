// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text;
using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;

namespace GhostShell.Data.Services;

internal sealed class RunService : IRunService
{
    private readonly DatabaseConnection _db;

    public RunService(DatabaseConnection db) => _db = db;

    // Single SELECT-list constant — used by ListAsync and GetAsync so
    // the column shape stays consistent and adding a column means
    // editing one spot.
    private const string SelectColumns = """
        id,
        profile_name   AS ProfileName,
        started_at     AS StartedAt,
        finished_at    AS FinishedAt,
        exit_code      AS ExitCode,
        total_queries  AS TotalQueries,
        total_ads      AS TotalAds,
        captchas,
        last_error     AS LastError,
        heartbeat_at   AS HeartbeatAt,
        stop_reason    AS StopReason
    """;

    public async Task<IReadOnlyList<Run>> ListAsync(
        int limit = 50,
        string? profileName = null,
        RunStatusFilter status = RunStatusFilter.All,
        int? sinceHours = null,
        CancellationToken ct = default)
    {
        var sb = new StringBuilder($"SELECT {SelectColumns} FROM runs WHERE 1=1 ");

        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            sb.Append(" AND profile_name = @profileName ");
            p.Add("profileName", profileName);
        }

        switch (status)
        {
            case RunStatusFilter.Successful:
                sb.Append(" AND exit_code = 0 ");
                break;
            case RunStatusFilter.Failed:
                sb.Append(" AND exit_code IS NOT NULL AND exit_code <> 0 ");
                break;
            case RunStatusFilter.Running:
                sb.Append(" AND finished_at IS NULL AND exit_code IS NULL ");
                break;
        }

        if (sinceHours is > 0)
        {
            sb.Append(" AND started_at >= @cutoff ");
            p.Add("cutoff", DateTime.UtcNow.AddHours(-sinceHours.Value).ToString("O"));
        }

        sb.Append(" ORDER BY started_at DESC LIMIT @limit; ");
        p.Add("limit", Math.Clamp(limit, 1, 1000));

        // QueueAsync wraps every read in the shared semaphore so a
        // concurrent RunsViewModel.ReloadAsync ↔ ProfilesViewModel
        // .ReloadAsync race never collides on the singleton
        // SqliteConnection's open-DataReader slot.
        var rows = await _db.QueueAsync(c => c.QueryAsync<Run>(sb.ToString(), p), ct);
        return rows.ToList();
    }

    public async Task<Run?> GetAsync(long runId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM runs WHERE id = @runId;";
        return await _db.QueueAsync(
            c => c.QuerySingleOrDefaultAsync<Run>(sql, new { runId }), ct);
    }

    public Task<long> StartAsync(string profileName, CancellationToken ct = default) =>
        // SQLite uses last_insert_rowid() for the autoincrement column.
        // RETURNING is supported on SQLite 3.35+ (Microsoft.Data.Sqlite
        // 8.x ships 3.43+) so we can grab the new id in one round-trip
        // instead of a follow-up SELECT.
        _db.QueueAsync(c => c.ExecuteScalarAsync<long>("""
            INSERT INTO runs (profile_name, started_at, heartbeat_at)
            VALUES (@ProfileName, @StartedAt, @StartedAt)
            RETURNING id;
            """, new
        {
            ProfileName = profileName,
            StartedAt   = DateTime.UtcNow.ToString("O"),
        }), ct);

    public Task FinishAsync(
        long runId, int exitCode,
        string? lastError = null, string? stopReason = null,
        CancellationToken ct = default) =>
        // The `finished_at IS NULL` guard makes the call idempotent —
        // if the watchdog AND the explicit Stop both race for the
        // same run, only the first wins. Without the guard the row
        // could oscillate exit_code values during teardown.
        _db.QueueAsync(c => c.ExecuteAsync("""
            UPDATE runs
               SET finished_at = @FinishedAt,
                   exit_code   = @ExitCode,
                   last_error  = @LastError,
                   stop_reason = @StopReason
             WHERE id          = @Id
               AND finished_at IS NULL;
            """, new
        {
            Id          = runId,
            FinishedAt  = DateTime.UtcNow.ToString("O"),
            ExitCode    = exitCode,
            LastError   = lastError,
            StopReason  = stopReason,
        }), ct);

    public Task HeartbeatAsync(long runId, CancellationToken ct = default) =>
        // Cheap UPDATE on the PK. The `finished_at IS NULL` guard
        // means a stale watchdog tick after we already marked the
        // run finished doesn't accidentally resurrect heartbeat
        // freshness on a finalised row.
        _db.QueueAsync(c => c.ExecuteAsync("""
            UPDATE runs
               SET heartbeat_at = @At
             WHERE id           = @Id
               AND finished_at  IS NULL;
            """, new
        {
            Id = runId,
            At = DateTime.UtcNow.ToString("O"),
        }), ct);

    public Task MarkFailedAsync(long runId, CancellationToken ct = default) =>
        // exit_code = -99 mirrors the legacy Python "user marked
        // failed" sentinel; the dashboard / Runs page renders -99
        // distinctly from a real crash exit.
        FinishAsync(runId, exitCode: -99,
            lastError: "Marked failed manually by user.",
            stopReason: "user_marked_failed", ct: ct);

    public Task<int> ClearAsync(DateTime? olderThan, CancellationToken ct = default)
    {
        // Active runs (finished_at IS NULL) are NEVER deleted by this
        // path — Clear is for tidying history, not killing live work.
        // Pass null olderThan → nuke all finished runs.
        var sql = "DELETE FROM runs WHERE finished_at IS NOT NULL ";
        var p = new DynamicParameters();
        if (olderThan is not null)
        {
            sql += " AND finished_at < @cutoff ";
            p.Add("cutoff", olderThan.Value.ToUniversalTime().ToString("O"));
        }
        sql += ";";
        return _db.QueueAsync(c => c.ExecuteAsync(sql, p), ct);
    }

    public Task<RunStats> GetStatsAsync(CancellationToken ct = default) =>
        // COALESCE wraps each SUM — on an empty `runs` table, SUM
        // returns NULL which Dapper can't map into a non-nullable int
        // and would throw. Wrapping with COALESCE(..., 0) keeps the
        // result well-defined for any row count, including zero.
        _db.QueueAsync(c => c.QuerySingleAsync<RunStats>("""
            SELECT
                COUNT(*) AS Total,
                COALESCE(SUM(CASE WHEN exit_code = 0 THEN 1 ELSE 0 END), 0)                                 AS Successful,
                COALESCE(SUM(CASE WHEN exit_code IS NOT NULL AND exit_code <> 0 THEN 1 ELSE 0 END), 0)      AS Failed,
                COALESCE(SUM(CASE WHEN finished_at IS NULL AND exit_code IS NULL THEN 1 ELSE 0 END), 0)     AS Running
            FROM runs;
            """), ct);

    public async Task<bool> DeleteAsync(long runId, CancellationToken ct = default)
    {
        // Phase 53 — delete a single finished run. Guard prevents
        // deletion of still-running rows. Returns true if deleted,
        // false if the row was still running or didn't exist.
        var affected = await _db.QueueAsync(c => c.ExecuteAsync("""
            DELETE FROM runs
             WHERE id = @Id
               AND finished_at IS NOT NULL;
            """, new { Id = runId }), ct);
        return affected > 0;
    }
}

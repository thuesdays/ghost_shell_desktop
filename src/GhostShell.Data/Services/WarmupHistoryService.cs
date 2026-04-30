// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// Persistence layer for <c>warmup_runs</c>. Owns the SQL — engine
/// orchestration lives in <c>WarmupService</c> (GhostShell.Runtime).
/// </summary>
public sealed class WarmupHistoryService : IWarmupHistoryService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<WarmupHistoryService> _log;

    public WarmupHistoryService(DatabaseConnection db, ILogger<WarmupHistoryService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string SelectColumns = """
        id,
        profile_name     AS ProfileName,
        started_at       AS StartedAt,
        finished_at      AS FinishedAt,
        preset           AS Preset,
        sites_planned    AS SitesPlanned,
        sites_visited    AS SitesVisited,
        sites_succeeded  AS SitesSucceeded,
        duration_sec     AS DurationSec,
        status           AS Status,
        trigger          AS Trigger,
        notes            AS Notes,
        sites_log        AS SitesLogJson
    """;

    public async Task<long> StartAsync(
        string profileName, string presetId, int sitesPlanned, string trigger,
        CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO warmup_runs
                (profile_name, started_at, preset, sites_planned, status, trigger, sites_log)
            VALUES
                (@ProfileName, @StartedAt, @Preset, @SitesPlanned, 'running', @Trigger, '[]')
            RETURNING id;
        """;
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            ProfileName  = profileName,
            StartedAt    = DateTime.UtcNow.ToString("O"),
            Preset       = presetId,
            SitesPlanned = sitesPlanned,
            Trigger      = trigger,
        }), ct);
        _log.LogInformation(
            "Warmup #{Id} started for '{Profile}' preset={Preset} planned={Planned} trigger={Trigger}",
            id, profileName, presetId, sitesPlanned, trigger);
        return id;
    }

    public async Task FinishAsync(
        long warmupId, string status, int sitesVisited, int sitesSucceeded,
        double durationSec, string? notes, string sitesLogJson,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE warmup_runs SET
                finished_at     = @FinishedAt,
                status          = @Status,
                sites_visited   = @SitesVisited,
                sites_succeeded = @SitesSucceeded,
                duration_sec    = @DurationSec,
                notes           = @Notes,
                sites_log       = @SitesLogJson
            WHERE id = @Id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            Id             = warmupId,
            FinishedAt     = DateTime.UtcNow.ToString("O"),
            Status         = status,
            SitesVisited   = sitesVisited,
            SitesSucceeded = sitesSucceeded,
            DurationSec    = durationSec,
            Notes          = notes,
            SitesLogJson   = sitesLogJson,
        }), ct);
        _log.LogInformation(
            "Warmup #{Id} finished status={Status} visited={V} succeeded={S} ({Dur:F1}s)",
            warmupId, status, sitesVisited, sitesSucceeded, durationSec);
    }

    public async Task<int> SweepOrphansAsync(CancellationToken ct = default)
    {
        // Mark every row stuck in 'running' as failed with an
        // "abandoned" note. We can't tell from the row alone whether
        // it's currently running or merely orphaned, but we run this
        // at app-startup BEFORE the engine accepts new requests, so
        // any 'running' row at that moment is by definition orphan.
        const string sql = """
            UPDATE warmup_runs SET
                status      = 'failed',
                notes       = COALESCE(notes, 'abandoned (app exited)'),
                finished_at = @FinishedAt
            WHERE status = 'running';
        """;
        var rows = await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            FinishedAt = DateTime.UtcNow.ToString("O"),
        }), ct);
        if (rows > 0)
            _log.LogInformation("Swept {Count} orphan warmup row(s) on startup", rows);
        return rows;
    }

    public Task<IReadOnlyList<WarmupRun>> ListAsync(
        string? profileName = null, int limit = 50, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
              FROM warmup_runs
             WHERE 1=1
        """;
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            sql += " AND profile_name = @ProfileName ";
            p.Add("ProfileName", profileName);
        }
        sql += " ORDER BY started_at DESC LIMIT @Limit; ";
        p.Add("Limit", Math.Clamp(limit, 1, 1000));

        return _db.QueueAsync<IReadOnlyList<WarmupRun>>(async c =>
            (await c.QueryAsync<WarmupRun>(sql, p)).ToList(), ct);
    }

    public Task<WarmupRun?> GetLatestAsync(string profileName, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM warmup_runs " +
                  "WHERE profile_name = @ProfileName " +
                  "ORDER BY started_at DESC LIMIT 1;";
        return _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<WarmupRun>(
            sql, new { ProfileName = profileName }), ct);
    }

    public Task<bool> IsRunningAsync(string profileName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT EXISTS(
                SELECT 1 FROM warmup_runs
                 WHERE profile_name = @ProfileName
                   AND status = 'running'
            );
        """;
        return _db.QueueAsync(c => c.ExecuteScalarAsync<bool>(
            sql, new { ProfileName = profileName }), ct);
    }
}

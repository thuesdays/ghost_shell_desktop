// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Background pruner: keeps each profile's snapshot list bounded so a
/// long-running install doesn't accumulate gigabytes of cookie JSON in
/// SQLite. Runs every <see cref="SweepInterval"/>; first sweep happens
/// 60s after startup so the rest of the host can settle.
///
/// Retention rules (in order):
///   1. Always keep the most recent <see cref="MinKeepPerProfile"/>
///      snapshots per profile, even if they're old.
///   2. Beyond that, drop snapshots older than <see cref="MaxAgeDays"/>.
///
/// Both rules are deliberately conservative — a destroyed snapshot is
/// gone for good (cookie JSON is irrecoverable), so we err toward
/// keeping more than needed. The thresholds can be promoted to
/// Settings later when the page exists.
/// </summary>
public sealed class SnapshotRetentionService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(6);
    private const int  MinKeepPerProfile           = 20;
    private const int  MaxAgeDays                  = 60;

    private readonly ISessionService _sessions;
    private readonly IProfileService _profiles;
    private readonly ILogger<SnapshotRetentionService> _log;

    public SnapshotRetentionService(
        ISessionService sessions,
        IProfileService profiles,
        ILogger<SnapshotRetentionService> log)
    {
        _sessions = sessions;
        _profiles = profiles;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Snapshot retention sweep failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var profiles = await _profiles.ListAsync(ct);
        var cutoff   = DateTime.UtcNow.AddDays(-MaxAgeDays);
        var totalDeleted = 0;

        foreach (var p in profiles)
        {
            ct.ThrowIfCancellationRequested();

            // ListAsync returns newest-first.
            var snapshots = await _sessions.ListAsync(p.Name, limit: 1000, ct);
            if (snapshots.Count <= MinKeepPerProfile) continue;

            // Skip the first MinKeep, then anything older than cutoff
            // gets pruned. Never touches the protected head.
            var prunable = snapshots
                .Skip(MinKeepPerProfile)
                .Where(s => ForceUtc(s.CreatedAt) < cutoff)
                .ToList();

            foreach (var snap in prunable)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _sessions.DeleteAsync(snap.Id, ct);
                    totalDeleted++;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Retention: delete snapshot #{Id} failed", snap.Id);
                }
            }
        }

        if (totalDeleted > 0)
            _log.LogInformation(
                "Snapshot retention swept {Count} old snapshot(s) (>{Days}d)",
                totalDeleted, MaxAgeDays);
    }

    /// <summary>
    /// Treat unspecified-kind timestamps as UTC. Microsoft.Data.Sqlite
    /// + Dapper round-trip DateTime via TEXT and the deserialiser
    /// returns Kind=Unspecified by default; comparing such a value to
    /// a UTC cutoff with the comparison operators silently does the
    /// "wrong" thing under a non-UTC system clock if you don't pin
    /// the kind first.
    /// </summary>
    private static DateTime ForceUtc(DateTime t)
        => t.Kind switch
        {
            DateTimeKind.Utc          => t,
            DateTimeKind.Local        => t.ToUniversalTime(),
            _                         => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        };
}

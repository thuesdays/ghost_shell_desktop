// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

public sealed class ScheduleService : IScheduleService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ScheduleService> _log;

    public ScheduleService(
        DatabaseConnection db,
        ILogger<ScheduleService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string SelectColumns = """
        id              AS Id,
        name            AS Name,
        target_kind     AS TargetKind,
        target_name     AS TargetName,
        trigger_kind    AS TriggerKind,
        cron_expr       AS CronExpr,
        interval_sec    AS IntervalSec,
        runs_per_day    AS RunsPerDay,
        min_jitter_sec  AS MinJitterSec,
        max_jitter_sec  AS MaxJitterSec,
        active_days     AS ActiveDays,
        active_from_hour AS ActiveFromHour,
        active_to_hour   AS ActiveToHour,
        enabled         AS Enabled,
        last_fired_at   AS LastFiredAt,
        next_fire_at    AS NextFireAt,
        fire_count      AS FireCount,
        fail_count      AS FailCount,
        created_at      AS CreatedAt,
        updated_at      AS UpdatedAt
    """;

    public async Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default)
    {
        var sql = $"""
            SELECT  {SelectColumns}
              FROM  schedules
          ORDER BY  enabled DESC, name COLLATE NOCASE;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<ScheduleRow>(sql), ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<Schedule?> GetAsync(long id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM schedules WHERE id = @id;";
        var row = await _db.QueueAsync(
            c => c.QuerySingleOrDefaultAsync<ScheduleRow>(sql, new { id }), ct);
        return row is null ? null : ToModel(row);
    }

    public async Task<Schedule> CreateAsync(Schedule s, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var row = ToRow(s) with
        {
            CreatedAt = s.CreatedAt == default ? now : s.CreatedAt,
            UpdatedAt = now,
        };

        const string sql = """
            INSERT INTO schedules
                (name, target_kind, target_name, trigger_kind,
                 cron_expr, interval_sec,
                 runs_per_day, min_jitter_sec, max_jitter_sec,
                 active_days,
                 active_from_hour, active_to_hour,
                 enabled, last_fired_at, next_fire_at,
                 fire_count, fail_count, created_at, updated_at)
            VALUES
                (@Name, @TargetKind, @TargetName, @TriggerKind,
                 @CronExpr, @IntervalSec,
                 @RunsPerDay, @MinJitterSec, @MaxJitterSec,
                 @ActiveDays,
                 @ActiveFromHour, @ActiveToHour,
                 @Enabled, @LastFiredAt, @NextFireAt,
                 @FireCount, @FailCount, @CreatedAt, @UpdatedAt);
            SELECT last_insert_rowid();
        """;
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, row), ct);
        _log.LogInformation(
            "Created schedule #{Id} '{Name}' → {Kind} '{Target}' ({Trigger})",
            id, s.Name, s.TargetKind, s.TargetName, s.TriggerKind);
        return ToModel(row with { Id = id });
    }

    public async Task UpdateAsync(Schedule s, CancellationToken ct = default)
    {
        var row = ToRow(s) with { UpdatedAt = DateTime.UtcNow };
        const string sql = """
            UPDATE schedules
               SET name             = @Name,
                   target_kind      = @TargetKind,
                   target_name      = @TargetName,
                   trigger_kind     = @TriggerKind,
                   cron_expr        = @CronExpr,
                   interval_sec     = @IntervalSec,
                   runs_per_day     = @RunsPerDay,
                   min_jitter_sec   = @MinJitterSec,
                   max_jitter_sec   = @MaxJitterSec,
                   active_days      = @ActiveDays,
                   active_from_hour = @ActiveFromHour,
                   active_to_hour   = @ActiveToHour,
                   enabled          = @Enabled,
                   last_fired_at    = @LastFiredAt,
                   next_fire_at     = @NextFireAt,
                   fire_count       = @FireCount,
                   fail_count       = @FailCount,
                   updated_at       = @UpdatedAt
             WHERE id               = @Id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql, row), ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM schedules WHERE id = @id;", new { id }), ct);
        _log.LogInformation("Deleted schedule #{Id}", id);
    }

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE schedules SET enabled = @e, updated_at = @now WHERE id = @id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql,
            new { id, e = enabled ? 1 : 0, now = DateTime.UtcNow }), ct);
    }

    public async Task<IReadOnlyList<Schedule>> GetDueAsync(DateTime now, CancellationToken ct = default)
    {
        // We compare ISO8601 strings — SQLite TEXT date comparison is
        // lexicographic and matches chronological order for the format
        // .NET emits via DateTime.ToString("O").
        var sql = $"""
            SELECT  {SelectColumns}
              FROM  schedules
             WHERE  enabled = 1
               AND  next_fire_at IS NOT NULL
               AND  next_fire_at <= @nowIso
          ORDER BY  next_fire_at ASC;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<ScheduleRow>(
            sql, new { nowIso = now.ToString("O") }), ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task RecordFiredAsync(
        long id, DateTime firedAt, DateTime? nextFireAt, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE schedules
               SET last_fired_at = @fired,
                   next_fire_at  = @next,
                   fire_count    = fire_count + 1,
                   fail_count    = 0,
                   updated_at    = @now
             WHERE id = @id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            id,
            fired = AsUtc(firedAt),
            next  = nextFireAt is null ? (DateTime?)null : AsUtc(nextFireAt.Value),
            now   = DateTime.UtcNow,
        }), ct);
    }

    public async Task RecordFailureAsync(
        long id, DateTime nextFireAt, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE schedules
               SET next_fire_at = @next,
                   fail_count   = fail_count + 1,
                   updated_at   = @now
             WHERE id = @id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            id,
            next = AsUtc(nextFireAt),
            now  = DateTime.UtcNow,
        }), ct);
    }

    public async Task RecordDeferralAsync(
        long id, DateTime nextFireAt, CancellationToken ct = default)
    {
        // Same shape as RecordFailureAsync but does NOT touch
        // fail_count. Used for active-window / runner-cap defers
        // where the schedule is healthy and just waiting its turn.
        const string sql = """
            UPDATE schedules
               SET next_fire_at = @next,
                   updated_at   = @now
             WHERE id = @id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            id,
            next = AsUtc(nextFireAt),
            now  = DateTime.UtcNow,
        }), ct);
    }

    /// <summary>Force-tag a DateTime as UTC. Inputs from the runner
    /// host are constructed via DateTime.UtcNow / .ToUniversalTime(),
    /// but a value with Kind=Unspecified would round-trip through
    /// SQLite TEXT and come back ambiguous — better to be explicit.</summary>
    private static DateTime AsUtc(DateTime t)
        => t.Kind switch
        {
            DateTimeKind.Utc         => t,
            DateTimeKind.Local       => t.ToUniversalTime(),
            _                        => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        };

    // ─── Mapping ─────────────────────────────────────────────────

    private sealed record ScheduleRow
    {
        public long Id { get; init; }
        public required string Name { get; init; }
        public required string TargetKind { get; init; }
        public required string TargetName { get; init; }
        public required string TriggerKind { get; init; }
        public string? CronExpr { get; init; }
        public int? IntervalSec { get; init; }
        public int? RunsPerDay { get; init; }
        public int? MinJitterSec { get; init; }
        public int? MaxJitterSec { get; init; }
        public string ActiveDays { get; init; } = "";
        public int? ActiveFromHour { get; init; }
        public int? ActiveToHour { get; init; }
        public int Enabled { get; init; }
        public DateTime? LastFiredAt { get; init; }
        public DateTime? NextFireAt { get; init; }
        public int FireCount { get; init; }
        public int FailCount { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private static Schedule ToModel(ScheduleRow r) => new()
    {
        Id          = r.Id,
        Name        = r.Name,
        TargetKind  = ParseEnum<ScheduleTargetKind>(r.TargetKind),
        TargetName  = r.TargetName,
        TriggerKind = ParseEnum<ScheduleTriggerKind>(r.TriggerKind),
        CronExpr    = r.CronExpr,
        IntervalSec = r.IntervalSec,
        RunsPerDay   = r.RunsPerDay,
        MinJitterSec = r.MinJitterSec,
        MaxJitterSec = r.MaxJitterSec,
        ActiveDays  = ParseDaysCsv(r.ActiveDays),
        ActiveFromHour = r.ActiveFromHour,
        ActiveToHour   = r.ActiveToHour,
        Enabled     = r.Enabled == 1,
        // Microsoft.Data.Sqlite + Dapper round-trip DateTime via TEXT
        // and the deserializer uses Kind=Unspecified by default. We
        // wrote everything as UTC; force the Kind back so consumers
        // calling .ToLocalTime() / .ToUniversalTime() get correct
        // results across DST boundaries.
        LastFiredAt = ForceUtc(r.LastFiredAt),
        NextFireAt  = ForceUtc(r.NextFireAt),
        FireCount   = r.FireCount,
        FailCount   = r.FailCount,
        CreatedAt   = ForceUtc(r.CreatedAt) ?? default,
        UpdatedAt   = ForceUtc(r.UpdatedAt) ?? default,
    };

    private static DateTime? ForceUtc(DateTime? t)
        => t is null ? null : DateTime.SpecifyKind(t.Value, DateTimeKind.Utc);

    private static ScheduleRow ToRow(Schedule s) => new()
    {
        Id          = s.Id,
        Name        = s.Name,
        TargetKind  = s.TargetKind.ToString().ToLowerInvariant(),
        TargetName  = s.TargetName,
        TriggerKind = s.TriggerKind.ToString().ToLowerInvariant(),
        CronExpr    = s.CronExpr,
        IntervalSec = s.IntervalSec,
        RunsPerDay   = s.RunsPerDay,
        MinJitterSec = s.MinJitterSec,
        MaxJitterSec = s.MaxJitterSec,
        ActiveDays  = string.Join(",", s.ActiveDays),
        ActiveFromHour = s.ActiveFromHour,
        ActiveToHour   = s.ActiveToHour,
        Enabled     = s.Enabled ? 1 : 0,
        LastFiredAt = s.LastFiredAt,
        NextFireAt  = s.NextFireAt,
        FireCount   = s.FireCount,
        FailCount   = s.FailCount,
        CreatedAt   = s.CreatedAt,
        UpdatedAt   = s.UpdatedAt,
    };

    private static T ParseEnum<T>(string raw) where T : struct, Enum
        => Enum.TryParse<T>(raw, ignoreCase: true, out var v) ? v : default;

    private static IReadOnlyList<int> ParseDaysCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<int>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0)
                  .Where(v => v is >= 1 and <= 7)
                  .Distinct()
                  .OrderBy(v => v)
                  .ToList();
    }
}

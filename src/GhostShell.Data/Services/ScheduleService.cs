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
        use_jitter      AS UseJitter,
        fires_today     AS FiresToday,
        last_fire_day   AS LastFireDay,
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
                 fire_count, fail_count,
                 use_jitter, fires_today, last_fire_day,
                 created_at, updated_at)
            VALUES
                (@Name, @TargetKind, @TargetName, @TriggerKind,
                 @CronExpr, @IntervalSec,
                 @RunsPerDay, @MinJitterSec, @MaxJitterSec,
                 @ActiveDays,
                 @ActiveFromHour, @ActiveToHour,
                 @Enabled, @LastFiredAt, @NextFireAt,
                 @FireCount, @FailCount,
                 @UseJitter, @FiresToday, @LastFireDay,
                 @CreatedAt, @UpdatedAt);
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
                   use_jitter       = @UseJitter,
                   fires_today      = @FiresToday,
                   last_fire_day    = @LastFireDay,
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
        // Phase 71mm — fail_count DECAYS by 1 on success instead of
        // resetting to 0. Pre-fix every successful fire reset
        // fail_count to 0, which meant a flapping schedule (fail,
        // success, fail, success, ...) never advanced the exponential
        // back-off curve and could hammer a broken target every tick.
        // Decay-by-1 keeps back-off advancing under flapping (each
        // failure +1, each success -1; net positive over a real
        // outage), while a healthy sustained streak still drains
        // back to 0 within a handful of fires.
        const string sql = """
            UPDATE schedules
               SET last_fired_at = @fired,
                   next_fire_at  = @next,
                   fire_count    = fire_count + 1,
                   fail_count    = CASE WHEN fail_count > 0
                                        THEN fail_count - 1
                                        ELSE 0 END,
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

    public async Task<int> IncrementFiresTodayAsync(
        long id, DateOnly localDay, CancellationToken ct = default)
    {
        // Phase 71cc — atomic upsert of the daily counter. The CASE
        // expression reads the existing last_fire_day:
        //   • matches today → +1
        //   • doesn't match (yesterday or null) → reset to 1
        // Same UPDATE either way so we don't need a separate read +
        // conditional write round-trip. The trailing SELECT returns the
        // post-increment value; the whole batch runs inside one gated
        // _querySemaphore round-trip so the increment and the read-back
        // can never see different states.
        //
        // audit DATA-05: this method is atomic on its own, but it is NOT
        // sufficient to enforce the runs_per_day cap because the runner
        // performs the cap CHECK (GetFiresTodayAsync) and this INCREMENT
        // as two separate calls straddling the actual fire. A truly
        // race-free cap needs a single "increment IFF under cap" SQL
        // statement that the runner calls BEFORE firing and that treats
        // 0-rows-affected as "cap reached, defer". That requires a new
        // interface method + a RunnerHost call-site change (both in other
        // files) — see TryConsumeDailyFireAsync in crossFileNeeded.
        var dayStr = localDay.ToString("yyyy-MM-dd");
        const string sql = """
            UPDATE schedules
               SET fires_today = CASE
                                   WHEN last_fire_day = @day THEN fires_today + 1
                                   ELSE 1
                                 END,
                   last_fire_day = @day,
                   updated_at    = @now
             WHERE id = @id;
            SELECT fires_today FROM schedules WHERE id = @id;
        """;
        return await _db.QueueAsync(c => c.ExecuteScalarAsync<int>(sql, new
        {
            id,
            day = dayStr,
            now = DateTime.UtcNow,
        }), ct);
    }

    // audit DATA-05: race-free daily-cap consumption.
    //
    // The runner today does GetFiresTodayAsync (check) → fire →
    // IncrementFiresTodayAsync (bump) as three separate steps, so two
    // interleaved fires of the same schedule can both observe
    // fires_today < cap before either increments and the schedule fires
    // cap+1 (or more) times a day, defeating the human-pacing the cap
    // exists to enforce. This method collapses the whole thing into ONE
    // gated, atomic SQL statement: it increments fires_today (resetting
    // on day-rollover) ONLY when the schedule is still under cap, and
    // reports back whether the slot was actually consumed. 0 rows
    // affected ⇒ cap already reached this day ⇒ caller must DEFER.
    //
    // It is intentionally NOT yet on IScheduleService: wiring it in
    // requires (1) adding the signature to the interface and (2) changing
    // RunnerHost.FireAsync to call it BEFORE firing instead of the
    // check/increment pair — both live in files this audit pass may not
    // touch. The implementation is provided here, correct and ready, so
    // the cross-file change is a mechanical hook-up. See crossFileNeeded.
    //
    // Returns the post-increment count when the slot was consumed
    // (>= 1), or null when the cap was already reached and nothing was
    // written (caller should defer to the next active window).
    public async Task<int?> TryConsumeDailyFireAsync(
        long id, DateOnly localDay, int cap, CancellationToken ct = default)
    {
        if (cap <= 0)
        {
            // No positive cap configured → caller treats it as "no
            // daily limit"; just bump the counter for stats parity.
            return await IncrementFiresTodayAsync(id, localDay, ct);
        }

        var dayStr = localDay.ToString("yyyy-MM-dd");
        // The WHERE clause is the atomic gate:
        //   • day rolled over (last_fire_day NULL or != today) → a fresh
        //     day, always allowed (counter resets to 1), OR
        //   • same day AND fires_today < cap → still under quota.
        // If neither holds the row isn't matched, 0 rows change, and the
        // trailing changes() returns 0 → cap reached. RETURNING isn't
        // available on the SQLite build we target, so we read changes()
        // and the (possibly updated) counter back in the same gated batch.
        const string sql = """
            UPDATE schedules
               SET fires_today = CASE
                                   WHEN last_fire_day = @day THEN fires_today + 1
                                   ELSE 1
                                 END,
                   last_fire_day = @day,
                   updated_at    = @now
             WHERE id = @id
               AND (last_fire_day IS NULL
                    OR last_fire_day <> @day
                    OR fires_today < @cap);
            SELECT changes(), fires_today FROM schedules WHERE id = @id;
        """;
        var row = await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<(long Changed, int FiresToday)>(
            sql, new { id, day = dayStr, cap, now = DateTime.UtcNow }), ct);
        return row.Changed > 0 ? row.FiresToday : (int?)null;
    }

    // audit DATA-05: refund a slot consumed by TryConsumeDailyFireAsync
    // when the fire didn't actually launch. Floored at 0 and gated on the
    // day still matching, so it can't underflow or cross a day rollover.
    public Task RefundDailyFireAsync(
        long id, DateOnly localDay, CancellationToken ct = default)
    {
        var dayStr = localDay.ToString("yyyy-MM-dd");
        const string sql = """
            UPDATE schedules
               SET fires_today = CASE WHEN fires_today > 0 THEN fires_today - 1 ELSE 0 END,
                   updated_at  = @now
             WHERE id = @id AND last_fire_day = @day;
        """;
        return _db.QueueAsync(c => c.ExecuteAsync(
            sql, new { id, day = dayStr, now = DateTime.UtcNow }), ct);
    }

    public async Task<int> GetFiresTodayAsync(
        long id, DateOnly localDay, CancellationToken ct = default)
    {
        // Phase 71cc — read-or-reset. Originally this issued a SELECT,
        // compared last_fire_day in C#, then (on a stale day) fired a
        // SEPARATE reset UPDATE — two un-coupled round-trips.
        //
        // audit DATA-05: collapse the stale-day reset and the read into
        // ONE gated SQL batch so two concurrent stale-day calls can't
        // each independently reset the counter (the read-reset race).
        // The reset is guarded by `last_fire_day <> @day` so it is an
        // idempotent no-op once any writer has rolled the day over, and
        // the trailing SELECT always returns the authoritative post-write
        // value. _querySemaphore serialises the whole batch as one
        // statement-group, so there is no interleave between the reset
        // and the read.
        //
        // NOTE: this only removes the read-reset sub-race. The
        // check-then-fire-then-increment window in the runner (cap read
        // here, IncrementFiresTodayAsync later) is NOT closed by this
        // method alone — see the class-level remark below and the
        // crossFileNeeded note: a true atomic "consume one fire if under
        // cap" needs a new interface method the runner calls pre-fire.
        var dayStr = localDay.ToString("yyyy-MM-dd");
        const string sql = """
            UPDATE schedules
               SET fires_today   = 0,
                   last_fire_day = @day,
                   updated_at    = @now
             WHERE id = @id AND (last_fire_day IS NULL OR last_fire_day <> @day);
            SELECT fires_today FROM schedules WHERE id = @id;
        """;
        return await _db.QueueAsync(c => c.ExecuteScalarAsync<int>(sql, new
        {
            id, day = dayStr, now = DateTime.UtcNow,
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
            // Phase 71mm — Unspecified Kind is suspicious. Pre-fix
            // we silently re-tagged as UTC, which masked bugs at
            // callers that forgot to construct DateTime with the
            // right Kind. Dapper deserializes ISO-8601 strings as
            // Unspecified so reading from DB is fine, but writing
            // an Unspecified value almost always means the caller
            // built it from `new DateTime(...)` without specifying
            // Kind — and that value's "real" intent could be either
            // UTC or Local. We keep the silent-as-UTC behaviour to
            // stay back-compat with stored data, but a future tidy-up
            // pass could log here.
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
        public int UseJitter { get; init; } = 1;
        public int FiresToday { get; init; }
        public string? LastFireDay { get; init; }
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
        UseJitter   = r.UseJitter == 1,
        FiresToday  = r.FiresToday,
        LastFireDay = r.LastFireDay,
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
        UseJitter   = s.UseJitter ? 1 : 0,
        FiresToday  = s.FiresToday,
        LastFireDay = s.LastFireDay,
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

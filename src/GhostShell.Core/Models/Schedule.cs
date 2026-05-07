// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One scheduled task. Mirrors the schedules table (Migration V8).
/// The runner-host's scheduler loop reads enabled rows and fires them
/// when their <see cref="NextFireAt"/> is reached.
/// </summary>
public sealed class Schedule
{
    public long Id { get; init; }
    public required string Name { get; init; }

    public ScheduleTargetKind  TargetKind  { get; init; } = ScheduleTargetKind.Profile;
    public required string     TargetName  { get; init; }

    public ScheduleTriggerKind TriggerKind { get; init; } = ScheduleTriggerKind.Interval;

    /// <summary>5-field cron expression (m h dom mon dow). Required when
    /// <see cref="TriggerKind"/> is <see cref="ScheduleTriggerKind.Cron"/>.</summary>
    public string? CronExpr { get; init; }

    /// <summary>Fixed interval in seconds. Required when
    /// <see cref="TriggerKind"/> is <see cref="ScheduleTriggerKind.Interval"/>.</summary>
    public int? IntervalSec { get; init; }

    /// <summary>Daily run target for <see cref="ScheduleTriggerKind.Simple"/>.
    /// The runner spreads this many fires across the active-hours
    /// window (or the full 24h day when no window is set). Combined
    /// with <see cref="UseJitter"/> the gap is either uniform
    /// (window/runs_per_day) or jittered ±50% around that mean.
    /// Required for Simple — null falls back to 24/day.</summary>
    public int? RunsPerDay { get; init; }

    /// <summary>Phase 71cc — when true (default) the gap between fires
    /// is randomised ±50% around the computed mean; when false fires
    /// are evenly spaced (mean only). Replaces the old min/max jitter
    /// fields which the user had to tune by hand.</summary>
    public bool UseJitter { get; init; } = true;

    /// <summary>Legacy — manual minimum gap. Pre-V28 rows used this
    /// directly. New rows leave it null and the runner derives the
    /// gap from <see cref="RunsPerDay"/> + active window. Kept for
    /// migration: rows from before the V28 schema change still
    /// surface their original values until re-edited.</summary>
    public int? MinJitterSec { get; init; }

    /// <summary>Legacy — manual maximum gap. See <see cref="MinJitterSec"/>.</summary>
    public int? MaxJitterSec { get; init; }

    /// <summary>Phase 71cc — persistent daily-fire counter. Survives
    /// app restarts (in-memory counter would reset to 0 on relaunch
    /// and let the user blow past the cap). Reset by the runner
    /// whenever <see cref="LastFireDay"/> doesn't match today's
    /// local calendar date.</summary>
    public int FiresToday { get; init; }

    /// <summary>Phase 71cc — local calendar date (yyyy-MM-dd) that
    /// <see cref="FiresToday"/> applies to. Null = never fired or
    /// pre-V28 row that hasn't been touched since the migration.</summary>
    public string? LastFireDay { get; init; }

    /// <summary>ISO weekday numbers (1=Mon … 7=Sun). Empty = every day.</summary>
    public IReadOnlyList<int> ActiveDays { get; init; } = Array.Empty<int>();

    /// <summary>Active-from hour 0-23 inclusive; null = any hour.</summary>
    public int? ActiveFromHour { get; init; }

    /// <summary>Active-to hour 0-23 inclusive; null = any hour.</summary>
    public int? ActiveToHour   { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTime? LastFiredAt { get; init; }
    public DateTime? NextFireAt  { get; init; }

    public int FireCount { get; init; }
    public int FailCount { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    // ─── Derived helpers ─────────────────────────────────────────

    /// <summary>Compact one-liner for the UI list — "every 30m",
    /// "0 9 * * 1-5", or "150/day, 20-180s gaps" depending on trigger kind.</summary>
    public string TriggerSummary => TriggerKind switch
    {
        ScheduleTriggerKind.Cron =>
            CronExpr ?? "(invalid cron)",
        ScheduleTriggerKind.Simple =>
            FormatSimple(RunsPerDay, UseJitter),
        _ => IntervalSec is { } s
                ? FormatInterval(s)
                : "(no interval)",
    };

    private static string FormatSimple(int? runs, bool useJitter)
    {
        var r = runs is > 0 ? $"{runs}/day" : "(no cap)";
        return useJitter ? $"{r}, randomised" : $"{r}, even spacing";
    }

    public string ActiveDaysSummary
    {
        get
        {
            if (ActiveDays.Count == 0) return "every day";
            if (ActiveDays.Count == 7) return "every day";
            string[] names = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
            return string.Join(", ",
                ActiveDays.Where(d => d is >= 1 and <= 7)
                          .OrderBy(d => d)
                          .Select(d => names[d - 1]));
        }
    }

    public string ActiveHoursSummary
        => ActiveFromHour is null && ActiveToHour is null
            ? "any time"
            : $"{ActiveFromHour ?? 0:D2}:00 – {ActiveToHour ?? 23:D2}:59";

    private static string FormatInterval(int seconds)
    {
        if (seconds < 60)         return $"every {seconds}s";
        if (seconds < 3600)       return $"every {seconds / 60}m";
        if (seconds < 86400)
        {
            var h = seconds / 3600;
            var m = (seconds % 3600) / 60;
            return m == 0 ? $"every {h}h" : $"every {h}h {m}m";
        }
        return $"every {seconds / 86400}d";
    }
}

/// <summary>What does the schedule fire? Either a single profile or
/// every member of a group.</summary>
public enum ScheduleTargetKind
{
    Profile,
    Group,
}

/// <summary>How does the scheduler decide when to fire?</summary>
public enum ScheduleTriggerKind
{
    /// <summary>Fixed interval in seconds.</summary>
    Interval,
    /// <summary>5-field cron expression.</summary>
    Cron,
    /// <summary>
    /// Human-pacing: target N runs per day spread across the active-
    /// hours window. The runner divides the window length by
    /// runs_per_day to get the mean gap; <see cref="Schedule.UseJitter"/>
    /// either uniformly spaces the fires or randomises the gap ±50%
    /// around that mean. Easier than cron for "fire ~150 times
    /// between 7am and 9pm" workloads.
    /// </summary>
    Simple,
}

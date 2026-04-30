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

    /// <summary>Soft daily target for <see cref="ScheduleTriggerKind.Simple"/>.
    /// The runner counts today's fires and stops once this is reached;
    /// resets at midnight (or at the start of the active-hours window
    /// the next day). Null = no cap.</summary>
    public int? RunsPerDay { get; init; }

    /// <summary>Minimum gap between fires in <see cref="ScheduleTriggerKind.Simple"/> mode.
    /// Each fire's gap is sampled uniformly from <c>[MinJitterSec, MaxJitterSec]</c>.</summary>
    public int? MinJitterSec { get; init; }

    /// <summary>Maximum gap between fires in <see cref="ScheduleTriggerKind.Simple"/> mode.</summary>
    public int? MaxJitterSec { get; init; }

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
            FormatSimple(RunsPerDay, MinJitterSec, MaxJitterSec),
        _ => IntervalSec is { } s
                ? FormatInterval(s)
                : "(no interval)",
    };

    private static string FormatSimple(int? runs, int? minJ, int? maxJ)
    {
        var parts = new List<string>();
        if (runs is { } r and > 0) parts.Add($"{r}/day");
        if (minJ is { } a and > 0 && maxJ is { } b and > 0)
            parts.Add($"{a}-{b}s gaps");
        return parts.Count == 0 ? "(simple)" : string.Join(", ", parts);
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
    /// Human-pacing: target N runs per day, each gap uniformly
    /// random in <c>[MinJitterSec, MaxJitterSec]</c>, optionally
    /// constrained to active hours. Easier than cron for "fire ~150
    /// times between 7am and 9pm with 20–180s gaps" workloads.
    /// </summary>
    Simple,
}

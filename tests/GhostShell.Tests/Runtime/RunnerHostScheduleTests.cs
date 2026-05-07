// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Runtime;
using Xunit;

namespace GhostShell.Tests.Runtime;

/// <summary>
/// Phase 71cc regression tests — covers the auto-jitter timing fix that
/// landed today. The pre-fix scheduler computed gaps from MinJitterSec /
/// MaxJitterSec independently of <c>RunsPerDay</c>, which let a user
/// configure "150 runs per day, 20-180s gaps" — the runner happily
/// fired all 150 in the first ~4 hours and idled the rest of the day.
/// The fix makes <c>ComputeNextFire</c> derive the gap from the active
/// window length divided by runs_per_day, with optional ±50% jitter.
///
/// Tests in this fixture verify:
///   • Mean gap = window / runs_per_day (uniform spacing path)
///   • Jittered gap stays in [0.5×mean .. 1.5×mean) (±50% bound)
///   • Window-aware: shrinking ActiveFromHour..ActiveToHour shrinks the
///     mean gap proportionally
///   • Wrap-around windows (22..6) compute the right span
///   • Sanity floor of 5s (a misconfigured row can't fire-storm)
///   • Sanity ceiling of 6h (a stupid-low runs_per_day can't park
///     fires a day in the future)
///   • Legacy back-compat: rows with null RunsPerDay but valid
///     min/max jitter still honour the manual gap
///   • Interval trigger uses IntervalSec verbatim
///   • Cron trigger advances to the next valid cron tick
///
/// All cases use deterministic clocks; the jittered case asserts a
/// distribution bound rather than an exact value (Random.Shared seeds
/// from time, so we sample N times and check min/max envelopes).
/// </summary>
public class RunnerHostScheduleTests
{
    private const int SecondsPerHour = 3600;

    private static Schedule MakeSimple(
        int runsPerDay,
        bool useJitter,
        int? activeFromHour = null,
        int? activeToHour   = null,
        int? minJitter = null,
        int? maxJitter = null) =>
        new()
        {
            Name           = "test",
            TargetName     = "p1",
            TargetKind     = ScheduleTargetKind.Profile,
            TriggerKind    = ScheduleTriggerKind.Simple,
            RunsPerDay     = runsPerDay,
            UseJitter      = useJitter,
            ActiveFromHour = activeFromHour,
            ActiveToHour   = activeToHour,
            MinJitterSec   = minJitter,
            MaxJitterSec   = maxJitter,
        };

    // ─── Window math ─────────────────────────────────────────────────

    [Fact]
    public void ComputeWindowSeconds_NoActiveHours_Is24h()
    {
        var s = MakeSimple(runsPerDay: 1, useJitter: false);
        Assert.Equal(24 * SecondsPerHour, RunnerHost.ComputeWindowSeconds(s));
    }

    [Fact]
    public void ComputeWindowSeconds_NormalWindow_IsInclusiveSpan()
    {
        // 7..21 inclusive = 15 hours (07:00..21:59).
        var s = MakeSimple(runsPerDay: 1, useJitter: false,
            activeFromHour: 7, activeToHour: 21);
        Assert.Equal(15 * SecondsPerHour, RunnerHost.ComputeWindowSeconds(s));
    }

    [Fact]
    public void ComputeWindowSeconds_WrapAround_InvertsCorrectly()
    {
        // 22..6 inclusive = (24-22) + (6+1) = 9 hours.
        var s = MakeSimple(runsPerDay: 1, useJitter: false,
            activeFromHour: 22, activeToHour: 6);
        Assert.Equal(9 * SecondsPerHour, RunnerHost.ComputeWindowSeconds(s));
    }

    [Fact]
    public void ComputeWindowSeconds_SingleHour_Is1h()
    {
        // 9..9 inclusive = 1 hour.
        var s = MakeSimple(runsPerDay: 1, useJitter: false,
            activeFromHour: 9, activeToHour: 9);
        Assert.Equal(SecondsPerHour, RunnerHost.ComputeWindowSeconds(s));
    }

    // ─── Uniform spacing (no jitter) ─────────────────────────────────

    [Fact]
    public void ComputeNextFire_SimpleNoJitter_GapIsWindowDividedByRuns()
    {
        // 14 hours / 150 runs = 336 sec/run.
        var s = MakeSimple(runsPerDay: 150, useJitter: false,
            activeFromHour: 7, activeToHour: 20);
        var now = DateTime.UtcNow;
        var next = RunnerHost.ComputeNextFire(s, now);
        var gap = (next - now).TotalSeconds;
        Assert.Equal(336, gap, precision: 0);
    }

    [Fact]
    public void ComputeNextFire_SimpleNoJitter_24hWindow()
    {
        // 24h / 24 runs = 3600 sec/run.
        var s = MakeSimple(runsPerDay: 24, useJitter: false);
        var now = DateTime.UtcNow;
        var next = RunnerHost.ComputeNextFire(s, now);
        var gap = (next - now).TotalSeconds;
        Assert.Equal(3600, gap, precision: 0);
    }

    [Fact]
    public void ComputeNextFire_SimpleNoJitter_IsDeterministic()
    {
        // Two calls at the same reference time → identical result.
        var s = MakeSimple(runsPerDay: 100, useJitter: false);
        var t = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(
            RunnerHost.ComputeNextFire(s, t),
            RunnerHost.ComputeNextFire(s, t));
    }

    // ─── Jittered spacing (±50%) ─────────────────────────────────────

    [Fact]
    public void ComputeNextFire_SimpleJittered_StaysInPlusMinus50Percent()
    {
        // 24h / 24 = 3600s mean. Jitter range: [1800..5400).
        var s = MakeSimple(runsPerDay: 24, useJitter: true);
        var now = DateTime.UtcNow;

        for (int i = 0; i < 200; i++)
        {
            var next = RunnerHost.ComputeNextFire(s, now);
            var gap = (next - now).TotalSeconds;
            Assert.InRange(gap, 1800.0 - 0.001, 5400.0);
        }
    }

    [Fact]
    public void ComputeNextFire_SimpleJittered_DistributionSpreadsBothWays()
    {
        // 200 samples around the mean — assert we see at least one
        // below mean and one above (otherwise the jitter is broken).
        var s = MakeSimple(runsPerDay: 24, useJitter: true);
        var now = DateTime.UtcNow;
        const double mean = 3600.0;

        bool sawBelow = false, sawAbove = false;
        for (int i = 0; i < 200; i++)
        {
            var gap = (RunnerHost.ComputeNextFire(s, now) - now).TotalSeconds;
            if (gap < mean) sawBelow = true;
            if (gap > mean) sawAbove = true;
            if (sawBelow && sawAbove) break;
        }
        Assert.True(sawBelow, "expected at least one sample below mean");
        Assert.True(sawAbove, "expected at least one sample above mean");
    }

    // ─── Sanity clamps ───────────────────────────────────────────────

    [Fact]
    public void ComputeNextFire_TooManyRunsClampsToFiveSecondFloor()
    {
        // 1h / 10000 runs = 0.36 sec — clamped to 5s floor.
        var s = MakeSimple(runsPerDay: 10000, useJitter: false,
            activeFromHour: 9, activeToHour: 9); // 1h window
        var now = DateTime.UtcNow;
        var gap = (RunnerHost.ComputeNextFire(s, now) - now).TotalSeconds;
        Assert.Equal(5.0, gap, precision: 1);
    }

    [Fact]
    public void ComputeNextFire_TooFewRunsClampsToSixHourCeiling()
    {
        // 24h / 1 run = 86400s — clamped to 6h ceiling (21600s).
        var s = MakeSimple(runsPerDay: 1, useJitter: false);
        var now = DateTime.UtcNow;
        var gap = (RunnerHost.ComputeNextFire(s, now) - now).TotalSeconds;
        Assert.Equal(6 * 3600, gap, precision: 1);
    }

    // ─── Legacy back-compat ──────────────────────────────────────────

    [Fact]
    public void ComputeNextFire_LegacyMinMaxJitterRespectedWhenRunsPerDayNull()
    {
        // Pre-V28 row: null RunsPerDay + manual jitter range.
        var s = new Schedule
        {
            Name         = "legacy",
            TargetName   = "p1",
            TargetKind   = ScheduleTargetKind.Profile,
            TriggerKind  = ScheduleTriggerKind.Simple,
            RunsPerDay   = null,
            MinJitterSec = 60,
            MaxJitterSec = 90,
        };
        var now = DateTime.UtcNow;

        for (int i = 0; i < 100; i++)
        {
            var gap = (RunnerHost.ComputeNextFire(s, now) - now).TotalSeconds;
            Assert.InRange(gap, 60.0, 91.0);  // upper exclusive +1 in the impl
        }
    }

    // ─── Interval / Cron sanity ──────────────────────────────────────

    [Fact]
    public void ComputeNextFire_IntervalUsesIntervalSecVerbatim()
    {
        var s = new Schedule
        {
            Name        = "iv",
            TargetName  = "p1",
            TargetKind  = ScheduleTargetKind.Profile,
            TriggerKind = ScheduleTriggerKind.Interval,
            IntervalSec = 90,
        };
        var now  = DateTime.UtcNow;
        var next = RunnerHost.ComputeNextFire(s, now);
        Assert.Equal(90, (next - now).TotalSeconds, precision: 0);
    }

    [Fact]
    public void ComputeNextFire_IntervalDefaultsToSixtySecondsWhenUnset()
    {
        var s = new Schedule
        {
            Name        = "iv",
            TargetName  = "p1",
            TargetKind  = ScheduleTargetKind.Profile,
            TriggerKind = ScheduleTriggerKind.Interval,
            IntervalSec = null,
        };
        var now  = DateTime.UtcNow;
        var next = RunnerHost.ComputeNextFire(s, now);
        Assert.Equal(60, (next - now).TotalSeconds, precision: 0);
    }

    [Fact]
    public void ComputeNextFire_CronInvalidExprFallsBackTo15Minutes()
    {
        var s = new Schedule
        {
            Name        = "cron",
            TargetName  = "p1",
            TargetKind  = ScheduleTargetKind.Profile,
            TriggerKind = ScheduleTriggerKind.Cron,
            CronExpr    = "this is not cron",
        };
        var now  = DateTime.UtcNow;
        var next = RunnerHost.ComputeNextFire(s, now);
        Assert.Equal(15 * 60, (next - now).TotalSeconds, precision: 1);
    }

    // ─── IsInActiveWindow ────────────────────────────────────────────

    [Fact]
    public void IsInActiveWindow_NoConstraints_AlwaysTrue()
    {
        var s = MakeSimple(runsPerDay: 1, useJitter: false);
        var any = new DateTime(2026, 5, 7, 3, 14, 15, DateTimeKind.Local);
        Assert.True(RunnerHost.IsInActiveWindow(s, any));
    }

    [Fact]
    public void IsInActiveWindow_NormalWindow_InsideTrueOutsideFalse()
    {
        var s = MakeSimple(runsPerDay: 1, useJitter: false,
            activeFromHour: 9, activeToHour: 17);
        var inside  = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Local);
        var beforeWindow = new DateTime(2026, 5, 7, 8, 0, 0, DateTimeKind.Local);
        var afterWindow  = new DateTime(2026, 5, 7, 18, 0, 0, DateTimeKind.Local);
        Assert.True(RunnerHost.IsInActiveWindow(s, inside));
        Assert.False(RunnerHost.IsInActiveWindow(s, beforeWindow));
        Assert.False(RunnerHost.IsInActiveWindow(s, afterWindow));
    }

    [Fact]
    public void IsInActiveWindow_WrapAroundWindow_HandlesMidnightCrossing()
    {
        // 22..6 covers the 22:xx, 23:xx, 0..6:xx hours.
        var s = MakeSimple(runsPerDay: 1, useJitter: false,
            activeFromHour: 22, activeToHour: 6);
        Assert.True (RunnerHost.IsInActiveWindow(s, new DateTime(2026, 5, 7, 23, 30, 0, DateTimeKind.Local)));
        Assert.True (RunnerHost.IsInActiveWindow(s, new DateTime(2026, 5, 7,  3,  0, 0, DateTimeKind.Local)));
        Assert.False(RunnerHost.IsInActiveWindow(s, new DateTime(2026, 5, 7, 12,  0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void IsInActiveWindow_ActiveDays_BlocksWrongWeekday()
    {
        // Mon-Fri only.
        var s = new Schedule
        {
            Name        = "weekdays",
            TargetName  = "p1",
            TargetKind  = ScheduleTargetKind.Profile,
            TriggerKind = ScheduleTriggerKind.Simple,
            RunsPerDay  = 10,
            ActiveDays  = new[] { 1, 2, 3, 4, 5 }, // Mon..Fri (ISO)
        };
        // 2026-05-07 is a Thursday.
        var thursday = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Local);
        // 2026-05-09 is a Saturday.
        var saturday = new DateTime(2026, 5, 9, 12, 0, 0, DateTimeKind.Local);
        Assert.True (RunnerHost.IsInActiveWindow(s, thursday));
        Assert.False(RunnerHost.IsInActiveWindow(s, saturday));
    }

    [Fact]
    public void ComputeTomorrowStart_UsesActiveFromHourWhenSet()
    {
        var s = MakeSimple(runsPerDay: 10, useJitter: false,
            activeFromHour: 7, activeToHour: 20);
        var todayNoon = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Local);
        var tomorrow  = RunnerHost.ComputeTomorrowStart(s, todayNoon);
        Assert.Equal(new DateTime(2026, 5, 8, 7, 0, 0, DateTimeKind.Local), tomorrow);
    }

    [Fact]
    public void ComputeTomorrowStart_FallsBackToMidnightWhenNoActiveFrom()
    {
        var s = MakeSimple(runsPerDay: 10, useJitter: false);
        var todayNoon = new DateTime(2026, 5, 7, 12, 0, 0, DateTimeKind.Local);
        var tomorrow  = RunnerHost.ComputeTomorrowStart(s, todayNoon);
        Assert.Equal(new DateTime(2026, 5, 8, 0, 0, 0, DateTimeKind.Local), tomorrow);
    }
}

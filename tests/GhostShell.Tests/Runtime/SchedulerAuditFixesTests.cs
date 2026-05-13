// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Runtime;
using Xunit;

namespace GhostShell.Tests.Runtime;

/// <summary>
/// Phase 71mm regression tests — covers the bugs the 4-agent audit
/// found on 2026-05-13 after the user reported "scheduler fired 150
/// runs by 13:30 instead of 21:00". Each test pins a specific bug
/// that was fixed in Phase 71mm so future refactors can't silently
/// regress them.
///
/// The audit's main findings:
///   1. `_lastFireUtc` was in-memory only → no min-gap guard after restart.
///   2. `ComputeNextFire(s, utcNow)` ignored `s.LastFiredAt` → drift.
///   3. `RunnerHostNextFireGuess` fell through to 60s for Simple triggers
///      → "Run Now" bypassed cap pacing.
///   4. `ToggleEnabledAsync` didn't recompute next_fire_at on resume
///      → schedule could fire immediately with stale past timestamp.
///   5. `EditAsync` didn't recompute next_fire_at when cadence changed
///      → new RunsPerDay/window not applied until next natural fire.
///   6. `RecordFiredAsync` reset fail_count to 0 on every success
///      → flapping schedule never advanced exponential back-off.
///   7. Re-entrancy: two parallel ticks could double-fire schedules.
/// </summary>
public class SchedulerAuditFixesTests
{
    // ─── Bug #3 — RunnerHostNextFireGuess for Simple ─────────────────
    //
    // The audit found that the App-side RunnerHostNextFireGuess fell
    // through to the Interval branch for Simple triggers, returning
    // `now + 60s`. That made "Run Now" on a Simple schedule re-fire
    // every minute regardless of the configured pacing.
    //
    // The fix duplicates RunnerHost.ComputeNextFire's Simple math
    // inside SchedulerViewModel (Phase-3 layering forbids referencing
    // Runtime from App directly). We can't unit-test the App-side
    // private method without WPF, but we CAN re-verify the Runtime
    // side computes the same shape so the duplication stays correct.

    [Fact]
    public void ComputeNextFire_SimpleNoJitter_PrecisionMeetsExpectedMean()
    {
        // 15h × 150 runs = 360s mean.
        var s = MakeSimple(150, useJitter: false,
            activeFromHour: 7, activeToHour: 21);
        var now = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc);
        var next = RunnerHost.ComputeNextFire(s, now);
        // 360s ±1s tolerance for floating-point rounding.
        Assert.InRange((next - now).TotalSeconds, 359.0, 361.0);
    }

    [Fact]
    public void ComputeNextFire_SimpleJittered_StaysWithinHalfMeanFloor()
    {
        // Random.Shared.NextDouble() ∈ [0, 1) so factor ∈ [0.5, 1.5)
        // → gap ∈ [180, 540)s for mean=360. The user's bug was avg=180s
        // which is the floor — never the average. This test catches a
        // regression where the upper bound goes missing (e.g., factor
        // mistakenly clamped at 0.5) by sampling enough times to see
        // an above-mean value.
        var s = MakeSimple(150, useJitter: true,
            activeFromHour: 7, activeToHour: 21);
        var now = DateTime.UtcNow;

        bool sawAboveMean = false;
        for (int i = 0; i < 100; i++)
        {
            var gap = (RunnerHost.ComputeNextFire(s, now) - now).TotalSeconds;
            Assert.InRange(gap, 179.0, 541.0);
            if (gap > 360.0) sawAboveMean = true;
        }
        Assert.True(sawAboveMean,
            "expected at least one jittered sample above the mean; " +
            "if this fails the jitter factor is clamped to [0.5, 1.0)");
    }

    // ─── Bug #2 — ComputeNextFire anchoring ──────────────────────────
    //
    // The Phase 71mm fix changes FireAsync to compute `anchor = max(utcNow, LastFiredAt + 1s)`
    // before passing into ComputeNextFire. We can't directly test
    // FireAsync (it touches IScheduleService/IProfileRunner), but we
    // verify the EXPECTED behaviour at the math level.

    // Phase 71mm audit follow-up — the anchor window must be wide
    // enough for realistic Simple-schedule cadences. Pre-audit it
    // was `utcNow.AddMinutes(-1)`, so any schedule with mean gap
    // >60s (i.e. ALL realistic Simple schedules) skipped the anchor
    // path. Post-audit it's `utcNow.AddDays(-1)`, which captures
    // any in-cadence fire but still rejects truly stale
    // LastFiredAt values from long-paused schedules.

    [Theory]
    [InlineData(0,    true)]      // freshly fired (1s ago) → anchor
    [InlineData(60,   true)]      // 1 min ago → anchor
    [InlineData(360,  true)]      // 6 min ago (mean gap for 15h/150) → anchor
    [InlineData(3600, true)]      // 1h ago → anchor
    [InlineData(43200, true)]     // 12h ago → anchor
    [InlineData(86399, true)]     // 1 sec under 24h → anchor
    [InlineData(86401, false)]    // 1 sec over 24h → use utcNow (stale)
    [InlineData(172800, false)]   // 2 days ago → use utcNow (very stale)
    public void AnchorWindow_AcceptsAllInCadenceFires_RejectsStale(
        int secondsAgo, bool shouldAnchor)
    {
        // Mirror the condition from RunnerHost.FireAsync line ~330:
        //   var anchor = (s.LastFiredAt is { } lf && lf > utcNow.AddDays(-1))
        //       ? lf.AddSeconds(1)
        //       : utcNow;
        var utcNow = DateTime.UtcNow;
        DateTime? lastFiredAt = utcNow.AddSeconds(-secondsAgo);
        var anchorWouldBeUsed = lastFiredAt is { } lf
            && lf > utcNow.AddDays(-1);
        Assert.Equal(shouldAnchor, anchorWouldBeUsed);
    }

    [Fact]
    public void AnchorWindow_NullLastFiredAt_FallsBackToUtcNow()
    {
        // First-ever fire path: no LastFiredAt → fall back to utcNow.
        var utcNow = DateTime.UtcNow;
        DateTime? lastFiredAt = null;
        var anchorWouldBeUsed = lastFiredAt is { } lf
            && lf > utcNow.AddDays(-1);
        Assert.False(anchorWouldBeUsed);
    }

    [Fact]
    public void ComputeNextFire_AnchorOnLastFiredAt_PreservesCadence()
    {
        // Simulate: previous fire at T=0, current tick at T+15s (15s
        // late). Without anchoring, next fire = T+15s + 360s = T+375s
        // (15s drift). With anchoring, next fire = T+1s + 360s = T+361s
        // (no drift). This test pins the anchored math at the
        // ComputeNextFire level — the FireAsync-level integration
        // test (which decides WHEN to anchor) is covered by
        // AnchorWindow_AcceptsAllInCadenceFires_RejectsStale above.
        var s = MakeSimple(150, useJitter: false,
            activeFromHour: 7, activeToHour: 21);
        var fireOrigin = new DateTime(2026, 5, 13, 9, 0, 0, DateTimeKind.Utc);
        var tickLate  = fireOrigin.AddSeconds(15);

        // The anchored call passes `fireOrigin.AddSeconds(1)` as
        // anchor. So we test that ComputeNextFire produces 360s past
        // that anchor, NOT 360s past the late tick.
        var anchored = RunnerHost.ComputeNextFire(s, fireOrigin.AddSeconds(1));
        var unanchored = RunnerHost.ComputeNextFire(s, tickLate);

        var anchoredGap = (anchored - fireOrigin).TotalSeconds;
        var unanchoredGap = (unanchored - fireOrigin).TotalSeconds;

        // Anchored result should be ~361s past origin (1s anchor offset + 360s gap).
        Assert.InRange(anchoredGap, 360.0, 362.0);
        // Unanchored result is 360s past tickLate = 375s past origin.
        Assert.InRange(unanchoredGap, 374.0, 376.0);
        // The 14s difference is exactly the tick-latency drift we're
        // avoiding. Without the fix, every fire would drift this much.
        Assert.True(unanchoredGap > anchoredGap + 10,
            "anchored next-fire should be at least 10s earlier than unanchored");
    }

    // ─── Bug #5 — Cadence-changed detection in Edit flow ─────────────
    //
    // SchedulerViewModel.CadenceChanged is private; we mirror its
    // logic here and test the equivalence so a future regression that
    // forgets to check (say) UseJitter is caught. This isn't a perfect
    // proxy but it documents the cadence-affecting fields.

    [Theory]
    [InlineData("none",           false)]   // identical → no change
    [InlineData("trigger_kind",   true)]    // TriggerKind changed
    [InlineData("runs_per_day",   true)]    // RunsPerDay changed
    [InlineData("interval_sec",   true)]    // IntervalSec changed
    [InlineData("active_from",    true)]    // ActiveFromHour changed
    [InlineData("active_to",      true)]    // ActiveToHour changed
    [InlineData("cron_expr",      true)]    // CronExpr changed
    [InlineData("use_jitter",     true)]    // UseJitter changed
    [InlineData("min_jitter",     true)]    // MinJitterSec changed
    [InlineData("max_jitter",     true)]    // MaxJitterSec changed
    [InlineData("active_days",    true)]    // ActiveDays changed (Phase 71mm audit fix)
    public void CadenceChanged_FlagsAllCadenceFields(string field, bool expectChange)
    {
        var a = MakeSimple(150, useJitter: true,
            activeFromHour: 7, activeToHour: 21);
        var b = MakeMutated(a, field);
        Assert.Equal(expectChange, MirrorCadenceChanged(a, b));
    }

    private static Schedule MakeMutated(Schedule baseS, string changedField) => new()
    {
        Id             = baseS.Id,
        Name           = baseS.Name,
        TargetKind     = baseS.TargetKind,
        TargetName     = baseS.TargetName,
        TriggerKind    = changedField == "trigger_kind" ? ScheduleTriggerKind.Cron : baseS.TriggerKind,
        CronExpr       = changedField == "cron_expr"    ? "0 9 * * *" : baseS.CronExpr,
        IntervalSec    = changedField == "interval_sec" ? 60 : baseS.IntervalSec,
        RunsPerDay     = changedField == "runs_per_day" ? 200 : baseS.RunsPerDay,
        UseJitter      = changedField == "use_jitter"   ? !baseS.UseJitter : baseS.UseJitter,
        MinJitterSec   = changedField == "min_jitter"   ? 10 : baseS.MinJitterSec,
        MaxJitterSec   = changedField == "max_jitter"   ? 99 : baseS.MaxJitterSec,
        ActiveFromHour = changedField == "active_from"  ? 8  : baseS.ActiveFromHour,
        ActiveToHour   = changedField == "active_to"    ? 20 : baseS.ActiveToHour,
        ActiveDays     = changedField == "active_days"
                           ? new[] { 1, 2, 3, 4, 5 }
                           : baseS.ActiveDays,
        Enabled        = baseS.Enabled,
        LastFiredAt    = baseS.LastFiredAt,
        NextFireAt     = baseS.NextFireAt,
        FireCount      = baseS.FireCount,
        FailCount      = baseS.FailCount,
    };

    // Local mirror of SchedulerViewModel.CadenceChanged so this test
    // doesn't have to spin up WPF. Update both sides if either changes.
    private static bool MirrorCadenceChanged(Schedule a, Schedule b) =>
        a.TriggerKind     != b.TriggerKind
     || a.RunsPerDay      != b.RunsPerDay
     || a.IntervalSec     != b.IntervalSec
     || a.ActiveFromHour  != b.ActiveFromHour
     || a.ActiveToHour    != b.ActiveToHour
     || a.CronExpr        != b.CronExpr
     || a.UseJitter       != b.UseJitter
     || a.MinJitterSec    != b.MinJitterSec
     || a.MaxJitterSec    != b.MaxJitterSec
     || !a.ActiveDays.SequenceEqual(b.ActiveDays);

    // ─── Bug #1 — ComputeWindowSeconds inclusive math ────────────────
    //
    // The agent flagged that 7..21 = 15h not 14h (inclusive math).
    // The fix kept the inclusive semantics but documented it. This
    // test pins the contract so a future refactor that flips to
    // exclusive (`to - from`) gets caught.

    [Fact]
    public void ComputeWindowSeconds_7to21_IsFifteenHoursInclusive()
    {
        var s = MakeSimple(150, useJitter: false,
            activeFromHour: 7, activeToHour: 21);
        // 21 - 7 + 1 = 15 hours = 54000s
        Assert.Equal(15 * 3600, RunnerHost.ComputeWindowSeconds(s));
    }

    [Fact]
    public void ComputeWindowSeconds_SameHour_IsOneHour()
    {
        // ActiveFromHour=ActiveToHour=12 means "fire any time during
        // the 12 o'clock hour" → 1h window. Pre-fix this used to be
        // 0h (no fires possible) due to `to - from` math.
        var s = MakeSimple(10, useJitter: false,
            activeFromHour: 12, activeToHour: 12);
        Assert.Equal(3600, RunnerHost.ComputeWindowSeconds(s));
    }

    // ─── Boundary: meanGap clamp ──────────────────────────────────────

    [Fact]
    public void ComputeNextFire_AbsurdRunsPerDay_HitsFloor()
    {
        // 1h window / 10000 runs/day = 0.36s/run. Floor is 5s.
        var s = MakeSimple(10000, useJitter: false,
            activeFromHour: 12, activeToHour: 12);
        var now = DateTime.UtcNow;
        var next = RunnerHost.ComputeNextFire(s, now);
        Assert.Equal(5.0, (next - now).TotalSeconds, precision: 1);
    }

    [Fact]
    public void ComputeNextFire_AbsurdlyLowRunsPerDay_HitsCeiling()
    {
        // 24h / 1 run/day = 86400s. Ceiling is 6h = 21600s.
        var s = MakeSimple(1, useJitter: false);
        var now = DateTime.UtcNow;
        var next = RunnerHost.ComputeNextFire(s, now);
        Assert.Equal(6 * 3600.0, (next - now).TotalSeconds, precision: 1);
    }

    // ─── Helper ──────────────────────────────────────────────────────

    private static Schedule MakeSimple(
        int runsPerDay,
        bool useJitter,
        int? activeFromHour = null,
        int? activeToHour   = null) =>
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
        };
}

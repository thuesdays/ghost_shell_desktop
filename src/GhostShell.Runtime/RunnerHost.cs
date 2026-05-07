// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime;

/// <summary>
/// Background scheduler tick. Owns the work loop that fires due
/// schedules into the runner.
///
/// Tick cadence: every <see cref="TickInterval"/> we ask the
/// schedule service for rows whose <c>next_fire_at</c> has passed,
/// then fire each in order. A schedule's next_fire_at is recomputed
/// on every fire (success or failure) so the loop never re-fires
/// the same row twice in one tick.
///
/// Concurrency rules:
///   • The runner's <see cref="IProfileRunner.ActiveProfileNames"/>
///     is the authority for "is this profile already live". A
///     schedule fire that targets an already-running profile is
///     skipped (can't open two Chromiums against one user-data-dir).
///   • The host process-wide cap <see cref="MaxParallelLaunches"/>
///     gates the total number of concurrent sessions. When the cap
///     is reached, due-but-blocked schedules wait their turn — we
///     re-check on the next tick.
///   • Failed launches bump <c>fail_count</c> and apply exponential
///     back-off (×2, ×4, ×8 capped at 1h) to the schedule's next
///     fire time so a wedged target doesn't keep retrying every
///     tick.
///
/// Active-window guards (active_days / active_from_hour /
/// active_to_hour) are checked at fire time. A schedule that's due
/// but currently outside its active window is rolled forward to the
/// next minute; the guard kicks in again on the next tick.
///
/// On shutdown: the cancellation token cancels the tick wait. We
/// don't try to stop in-flight launches — the existing app-shutdown
/// orchestration (AppShutdown) handles browser teardown.
/// </summary>
public sealed class RunnerHost : IHostedService, IDisposable
{
    /// <summary>How often we poll the schedule store for due rows.
    /// 30s gives us minute-granularity firing without hammering
    /// SQLite — scheduled tasks aren't sub-minute precise anyway
    /// (cron resolution is one minute).</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>Maximum concurrent runs across the whole runner.
    /// TODO Phase 6: surface as a Settings.json value. Mirrors the
    /// legacy <c>runner.max_parallel</c> config key (default 4).</summary>
    public const int MaxParallelLaunches = 4;

    private readonly IScheduleService _schedules;
    private readonly IProfileService  _profiles;
    private readonly IProfileGroupService _groups;
    private readonly IProfileRunner   _runner;
    private readonly IUpdateService _update;
    private readonly ILogger<RunnerHost> _log;

    private CancellationTokenSource? _cts;
    private Task? _tickLoop;

    // Phase 71cc — daily-fire counters now live in the schedules table
    // (fires_today + last_fire_day columns). The in-memory cache here
    // is just a small write-through optimisation: each tick reads
    // fires_today via IScheduleService.GetFiresTodayAsync (which
    // self-resets when the day rolls). Removed the old in-memory
    // _dailyFires dict because it lost state on every app restart and
    // let the user blow past the runs_per_day cap by relaunching.

    public RunnerHost(
        IScheduleService schedules,
        IProfileService  profiles,
        IProfileGroupService groups,
        IProfileRunner   runner,
        IUpdateService update,
        ILogger<RunnerHost> log)
    {
        _schedules = schedules;
        _profiles  = profiles;
        _groups    = groups;
        _runner    = runner;
        _update    = update;
        _log       = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tickLoop = Task.Run(() => RunTickLoopAsync(_cts.Token), _cts.Token);
        _log.LogInformation(
            "RunnerHost started: scheduler tick every {Tick}, max parallel {Cap}",
            TickInterval, MaxParallelLaunches);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("RunnerHost stopping…");
        _cts?.Cancel();
        if (_tickLoop is not null)
        {
            try
            {
                // Best-effort: wait up to 5s for the loop to drain.
                await Task.WhenAny(_tickLoop, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
            }
            catch (OperationCanceledException) { /* expected */ }
        }
        _log.LogInformation("RunnerHost stopped.");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task RunTickLoopAsync(CancellationToken ct)
    {
        // Small startup delay so the migration runner + DI bootstrap
        // settle before our first DB hit. Keeps the boot log tidy.
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Never let the tick loop die — log and keep going.
                // A schedule with a corrupt cron expression shouldn't
                // take down the whole runner.
                _log.LogError(ex, "Scheduler tick failed; will retry next interval");
            }

            try { await Task.Delay(TickInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One pass through the scheduler: find due rows, fire each in
    /// order. Public-internal scope so tests can drive a single tick
    /// without spinning up the host loop.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        // Phase 71 — if an update is preparing, skip this tick so active
        // runs can drain naturally without the scheduler firing new ones.
        if (_update.IsUpdatePending)
        {
            _log.LogDebug("Scheduler tick skipped — update is preparing");
            return;
        }

        // Snapshot BOTH time references at the top of the tick so all
        // downstream comparisons see the same moment. UTC is what
        // SQLite uses for the GetDueAsync comparison; local is what
        // active-window guards (active_from_hour, active_days) need.
        // Computing them at the call site each time risked DST edge
        // cases where the local-snapshot was a millisecond ahead of
        // utcNow → the schedule appears to be deferred AND fired.
        var utcNow   = DateTime.UtcNow;
        var localNow = utcNow.ToLocalTime();

        var due = await _schedules.GetDueAsync(utcNow, ct);
        if (due.Count == 0) return;

        // Process oldest-due first. The service already orders by
        // next_fire_at ASC; we just iterate.
        foreach (var schedule in due)
        {
            ct.ThrowIfCancellationRequested();
            await FireAsync(schedule, utcNow, localNow, ct);
        }
    }

    /// <summary>
    /// Fire a single schedule. Routes to profile-launch or group-
    /// launch depending on <see cref="Schedule.TargetKind"/>.
    /// Updates <c>last_fired_at</c>/<c>next_fire_at</c>/<c>fire_count</c>
    /// on success or <c>fail_count</c> on real failure. Active-window
    /// and runner-cap deferrals route through
    /// <see cref="IScheduleService.RecordDeferralAsync"/> so they don't
    /// pollute the back-off curve.
    /// </summary>
    private async Task FireAsync(Schedule s, DateTime utcNow, DateTime localNow, CancellationToken ct)
    {
        // Phase 71cc — outside-active-window: defer to the START of
        // the next active window (today if the window hasn't begun
        // yet, tomorrow's start if we've passed today's end). Old
        // code pushed +1 minute, which produced thousands of
        // overnight DB updates and made the activity log unreadable.
        if (!IsInActiveWindow(s, localNow))
        {
            var nextWindowLocal = ComputeNextWindowStart(s, localNow);
            var nextWindowUtc = DateTime.SpecifyKind(nextWindowLocal, DateTimeKind.Local).ToUniversalTime();
            await _schedules.RecordDeferralAsync(s.Id, nextWindowUtc, ct);
            _log.LogInformation(
                "Schedule #{Id} '{Name}' outside active window — defer to {Next}",
                s.Id, s.Name, nextWindowUtc);
            return;
        }

        // Phase 71cc — persistent daily-fire cap (Simple trigger).
        // The counter lives in the schedules.fires_today column so it
        // survives app restarts. Pre-fix: in-memory counter reset to
        // zero on every relaunch and the user could blow past the cap
        // by relaunching mid-day.
        if (s.TriggerKind == ScheduleTriggerKind.Simple
            && s.RunsPerDay is { } cap and > 0)
        {
            var todayCount = await _schedules.GetFiresTodayAsync(
                s.Id, DateOnly.FromDateTime(localNow), ct);
            if (todayCount >= cap)
            {
                var tomorrow = ComputeTomorrowStart(s, localNow);
                var nextUtc = DateTime.SpecifyKind(tomorrow, DateTimeKind.Local).ToUniversalTime();
                await _schedules.RecordDeferralAsync(s.Id, nextUtc, ct);
                _log.LogInformation(
                    "Schedule #{Id} '{Name}' hit daily cap {Cap} — defer to {Next}",
                    s.Id, s.Name, cap, nextUtc);
                return;
            }
        }

        // Concurrency cap. ActiveProfileNames returns a snapshot;
        // we re-check inside Fire*Async right before StartAsync to
        // close the TOCTOU window. Outer check is just a fast bail
        // for the common "queue is full" case.
        if (_runner.ActiveProfileNames.Count >= MaxParallelLaunches)
        {
            var nextSlot = utcNow.Add(TickInterval);
            await _schedules.RecordDeferralAsync(s.Id, nextSlot, ct);
            _log.LogInformation(
                "Schedule #{Id} '{Name}' deferred — runner cap {Cap} reached",
                s.Id, s.Name, MaxParallelLaunches);
            return;
        }

        var outcome = FireOutcome.Failed;
        try
        {
            outcome = s.TargetKind switch
            {
                ScheduleTargetKind.Profile => await FireProfileAsync(s.TargetName, ct),
                ScheduleTargetKind.Group   => await FireGroupAsync(s.TargetName, ct),
                _ => FireOutcome.Failed,
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Schedule #{Id} '{Name}' fire threw — bumping fail_count",
                s.Id, s.Name);
        }

        var nextFire = ComputeNextFire(s, utcNow);

        if (outcome == FireOutcome.Deferred)
        {
            // Cap raced between outer check and StartAsync, OR profile
            // is already running (manual launch). Healthy schedule,
            // just bad timing — push next_fire_at forward but DON'T
            // bump fire_count or fail_count. Pre-fix: hitting an
            // already-running profile incremented fire_count which
            // inflated stats and spammed the activity log.
            await _schedules.RecordDeferralAsync(s.Id, nextFire, ct);
            _log.LogDebug(
                "Schedule #{Id} '{Name}' deferred — target busy, retry at {Next}",
                s.Id, s.Name, nextFire);
            return;
        }

        if (outcome == FireOutcome.Launched)
        {
            // Phase 71cc — persistent counter bump for Simple schedules.
            // Atomic UPSERT in the SQL — handles the day-rollover case
            // (yesterday's row → today gets reset to 1 instead of N+1).
            if (s.TriggerKind == ScheduleTriggerKind.Simple)
                await _schedules.IncrementFiresTodayAsync(
                    s.Id, DateOnly.FromDateTime(localNow), ct);

            await _schedules.RecordFiredAsync(s.Id, utcNow, nextFire, ct);
            _log.LogInformation(
                "Schedule #{Id} '{Name}' fired ({Kind} '{Target}') → next {Next}",
                s.Id, s.Name, s.TargetKind, s.TargetName, nextFire);
        }
        else
        {
            // Real failure path — bump fail_count and apply
            // exponential back-off (×2, ×4, ×8…) capped at 1h. Bit-
            // shift instead of Math.Pow → avoids any double→int
            // rounding artefacts at higher fail counts.
            var clamped = Math.Min(s.FailCount, 7);
            var backOffSec = Math.Min(3600, 30 * (1 << clamped));
            var backOff   = TimeSpan.FromSeconds(backOffSec);
            var deferred  = utcNow.Add(backOff);
            // If the schedule's natural next-fire is later than the
            // back-off would push us, prefer the natural one.
            if (nextFire > deferred) deferred = nextFire;
            await _schedules.RecordFailureAsync(s.Id, deferred, ct);
            _log.LogWarning(
                "Schedule #{Id} '{Name}' fire FAILED ({Kind} '{Target}') → " +
                "next {Next} (back-off {BackOff}s, fail_count {Fc})",
                s.Id, s.Name, s.TargetKind, s.TargetName, deferred,
                backOffSec, s.FailCount + 1);
        }
    }

    private async Task<FireOutcome> FireProfileAsync(string profileName, CancellationToken ct)
    {
        if (_runner.ActiveProfileNames.Contains(profileName))
        {
            // Phase 71cc — already running (e.g. user clicked Start
            // manually, or previous run is still in flight). Old
            // code returned Launched which bumped fire_count + spammed
            // "Schedule fired …" in the log every 30s while the
            // run-in-progress chugged through its script. Return
            // Deferred instead: scheduler simply pushes next_fire_at
            // forward without altering counters or noise.
            _log.LogDebug(
                "Schedule fire skipped: profile '{Name}' already active",
                profileName);
            return FireOutcome.Deferred;
        }
        var profile = await _profiles.GetAsync(profileName, ct);
        if (profile is null)
        {
            _log.LogWarning(
                "Schedule fire failed: profile '{Name}' not found", profileName);
            return FireOutcome.Failed;
        }

        // TOCTOU re-check. Outer FireAsync's cap check is a snapshot;
        // between then and now a manual launch (Profiles page) could
        // push us over. Treat as a deferral — healthy schedule, just
        // bad timing.
        if (_runner.ActiveProfileNames.Count >= MaxParallelLaunches)
        {
            _log.LogInformation(
                "Schedule fire deferred: cap {Cap} reached just before launch '{Name}'",
                MaxParallelLaunches, profileName);
            return FireOutcome.Deferred;
        }

        await _runner.StartAsync(profile);
        return FireOutcome.Launched;
    }

    private async Task<FireOutcome> FireGroupAsync(string groupName, CancellationToken ct)
    {
        var groups = await _groups.ListAsync(ct);
        var match = groups.FirstOrDefault(g =>
            string.Equals(g.Name, groupName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _log.LogWarning(
                "Schedule fire failed: group '{Name}' not found", groupName);
            return FireOutcome.Failed;
        }
        var detailed = await _groups.GetAsync(match.Id, ct);
        if (detailed is null || detailed.Members.Count == 0)
        {
            _log.LogWarning(
                "Schedule fire failed: group '{Name}' empty or missing", groupName);
            return FireOutcome.Failed;
        }

        // Group cap overrides the host cap when set, else host cap.
        // We start members one at a time, re-checking the active
        // count before EACH launch (TOCTOU-safe). If the cap was
        // already exhausted by the time we got here, defer.
        var groupCap = detailed.MaxParallel ?? MaxParallelLaunches;
        var hostCap  = MaxParallelLaunches;
        var effectiveCap = Math.Min(groupCap, hostCap);
        if (_runner.ActiveProfileNames.Count >= effectiveCap)
            return FireOutcome.Deferred;

        var launchedAny = false;
        foreach (var name in detailed.Members)
        {
            ct.ThrowIfCancellationRequested();
            if (_runner.ActiveProfileNames.Count >= effectiveCap) break;
            if (_runner.ActiveProfileNames.Contains(name)) continue;
            var profile = await _profiles.GetAsync(name, ct);
            if (profile is null) continue;
            try
            {
                await _runner.StartAsync(profile);
                launchedAny = true;
                // Tiny stagger so chromedriver doesn't see N parallel
                // boot races for unique --remote-debugging-port slots.
                await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Group '{Group}' member '{Profile}' launch failed",
                    groupName, name);
            }
        }
        return launchedAny ? FireOutcome.Launched : FireOutcome.Failed;
    }

    /// <summary>
    /// What happened when a schedule fired.
    /// <list type="bullet">
    ///   <item><c>Launched</c> — at least one profile started; bump fire_count.</item>
    ///   <item><c>Failed</c>   — target missing / launch threw; bump fail_count.</item>
    ///   <item><c>Deferred</c> — schedule healthy, runner cap raced; just push next_fire_at.</item>
    /// </list>
    /// </summary>
    private enum FireOutcome { Launched, Failed, Deferred, }

    /// <summary>
    /// Compute the next fire time for a schedule given the current
    /// reference moment. Public so tests can lock the schedule's
    /// invariants.
    ///
    /// Phase 71cc — Simple trigger now derives the gap from the active
    /// window length and runs_per_day. <see cref="Schedule.UseJitter"/>
    /// either jitters ±50% around the mean or fires uniformly. The
    /// legacy MinJitterSec / MaxJitterSec fields are honoured ONLY if
    /// runs_per_day is null (back-compat for very old rows).
    /// </summary>
    public static DateTime ComputeNextFire(Schedule s, DateTime referenceUtc)
    {
        switch (s.TriggerKind)
        {
            case ScheduleTriggerKind.Cron:
            {
                var cron = CronExpression.TryParse(s.CronExpr, out _);
                if (cron is null) return referenceUtc.AddMinutes(15); // safe fallback
                // Cron operates on local time (the user's wall clock).
                var localRef = referenceUtc.ToLocalTime();
                var nextLocal = cron.NextAfter(localRef);
                if (nextLocal is null) return referenceUtc.AddDays(1);
                return nextLocal.Value.ToUniversalTime();
            }

            case ScheduleTriggerKind.Simple:
            {
                // Phase 71cc — auto-compute gap from window/runs.
                // Window length in seconds. If both ActiveFromHour
                // and ActiveToHour are set, use that span; else the
                // full 24h day. Wrap-around windows (e.g. 22..6) are
                // counted as the inverted span.
                var windowSec = ComputeWindowSeconds(s);
                var runs = s.RunsPerDay is > 0 ? s.RunsPerDay.Value : 24;

                // Legacy back-compat: if runs_per_day is null/0 but
                // the row carries a manual MinJitter/MaxJitter pair,
                // honour it. Lets pre-V28 rows keep working until
                // they get re-edited.
                if ((s.RunsPerDay is null or 0)
                    && s.MinJitterSec is > 0
                    && s.MaxJitterSec is > 0
                    && s.MaxJitterSec.Value >= s.MinJitterSec.Value)
                {
                    var legacyGap = Random.Shared.Next(
                        s.MinJitterSec.Value, s.MaxJitterSec.Value + 1);
                    return referenceUtc.AddSeconds(legacyGap);
                }

                // Mean gap = window / runs. e.g. 14h × 150 → 336 sec.
                var meanGap = (double)windowSec / runs;
                // Sanity floor + ceiling so a misconfigured row (1
                // run / 24h or 10000 runs / 1h) doesn't produce a 0s
                // or hour-long gap. Floor 5s — runner can't physically
                // cycle Chromium any faster. Ceiling 6h — past that,
                // a Cron rule is what the user actually wants.
                meanGap = Math.Clamp(meanGap, 5.0, 6 * 3600.0);

                double gapSec;
                if (s.UseJitter)
                {
                    // Random ±50% around the mean. NextDouble() is
                    // [0..1) so this gives [0.5*mean .. 1.5*mean).
                    var factor = 0.5 + Random.Shared.NextDouble();
                    gapSec = meanGap * factor;
                }
                else
                {
                    // Uniform spacing — every fire exactly meanGap
                    // seconds apart.
                    gapSec = meanGap;
                }
                return referenceUtc.AddSeconds(gapSec);
            }

            // Interval — fire N seconds from the reference moment. We use
            // referenceUtc rather than s.LastFiredAt so a schedule that's
            // been disabled for a while picks up "from now" instead of
            // chain-firing every backed-up interval.
            default:
            {
                var seconds = s.IntervalSec is > 0 ? s.IntervalSec.Value : 60;
                return referenceUtc.AddSeconds(seconds);
            }
        }
    }

    /// <summary>Phase 71cc — compute the active-window length in
    /// seconds. ActiveFromHour/ActiveToHour are inclusive whole hours
    /// (e.g. 7..21 = "between 7:00 and 21:59"). Empty window = 24h.
    /// Wrap-around windows (22..6) are correctly inverted.</summary>
    public static int ComputeWindowSeconds(Schedule s)
    {
        if (s.ActiveFromHour is { } from && s.ActiveToHour is { } to)
        {
            int hours = to >= from
                ? (to - from + 1)
                : (24 - from) + (to + 1);
            return hours * 3600;
        }
        return 24 * 3600;
    }

    /// <summary>
    /// Returns true if <paramref name="localNow"/> falls inside
    /// the schedule's active days + hours window.
    /// </summary>
    public static bool IsInActiveWindow(Schedule s, DateTime localNow)
    {
        // Active days. Empty set = every day.
        if (s.ActiveDays.Count > 0)
        {
            // .NET DayOfWeek: Sun=0..Sat=6 → ISO 1=Mon..7=Sun.
            var iso = (int)localNow.DayOfWeek;
            iso = iso == 0 ? 7 : iso;
            if (!s.ActiveDays.Contains(iso)) return false;
        }

        // Active hours. Both null = any time. Otherwise inclusive
        // [from..to]. We support windows that wrap midnight (e.g.
        // 22..6) by inverting the comparison.
        if (s.ActiveFromHour is { } fromH && s.ActiveToHour is { } toH)
        {
            var h = localNow.Hour;
            return fromH <= toH
                ? h >= fromH && h <= toH
                : h >= fromH || h <= toH;
        }
        if (s.ActiveFromHour is { } fOnly) return localNow.Hour >= fOnly;
        if (s.ActiveToHour   is { } tOnly) return localNow.Hour <= tOnly;
        return true;
    }

    /// <summary>
    /// Compute "tomorrow's window start" in local time for a schedule
    /// that hit its daily cap. Uses ActiveFromHour if set, else 00:00.
    /// Returned DateTime carries Kind=Local so callers can convert to
    /// UTC explicitly — we don't tag here because mixing UTC/Local
    /// with TimeZoneInfo is a known footgun across DST transitions.
    /// </summary>
    public static DateTime ComputeTomorrowStart(Schedule s, DateTime localNow)
    {
        var fromHour = s.ActiveFromHour ?? 0;
        var tomorrow = localNow.Date.AddDays(1).AddHours(fromHour);
        return tomorrow;
    }

    /// <summary>
    /// Phase 71cc — given a schedule that's currently outside its
    /// active window, compute when the next active window starts
    /// (today's window-start if we haven't reached it yet, otherwise
    /// tomorrow's). This replaces the old "+1 minute" deferral that
    /// produced thousands of overnight DB writes.
    /// </summary>
    public static DateTime ComputeNextWindowStart(Schedule s, DateTime localNow)
    {
        var fromHour = s.ActiveFromHour ?? 0;
        // If both day and hour guards say "wait until Xh today" and we
        // haven't reached that hour yet, use today's window start.
        var todayWindow = localNow.Date.AddHours(fromHour);
        if (todayWindow > localNow && IsActiveDay(s, todayWindow))
            return todayWindow;
        // Otherwise scan forward day-by-day until we find an active day
        // — handles the case where the user restricted to e.g. weekdays
        // and we're firing late on a Friday → next active is Monday's
        // window-start.
        for (int i = 1; i <= 7; i++)
        {
            var candidate = localNow.Date.AddDays(i).AddHours(fromHour);
            if (IsActiveDay(s, candidate)) return candidate;
        }
        // No active day in the next 7 days — schedule is dead. Push
        // far enough forward that the runner doesn't burn cycles on
        // it; an explicit user re-enable can re-arm it.
        return localNow.Date.AddDays(7).AddHours(fromHour);
    }

    private static bool IsActiveDay(Schedule s, DateTime localDay)
    {
        if (s.ActiveDays.Count == 0) return true;
        var iso = (int)localDay.DayOfWeek;
        iso = iso == 0 ? 7 : iso;
        return s.ActiveDays.Contains(iso);
    }
}

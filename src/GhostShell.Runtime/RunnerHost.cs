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
    private readonly ILogger<RunnerHost> _log;

    private CancellationTokenSource? _cts;
    private Task? _tickLoop;

    /// <summary>
    /// In-memory daily-fire counters for Simple-trigger schedules
    /// with a runs_per_day cap. Keyed by schedule id; the tuple is
    /// (LocalDate, FiresToday). When the LocalDate changes, the
    /// counter is reset on next access.
    ///
    /// Lives in memory only — across app restarts the cap effectively
    /// resets, which is fine because the schedule's next_fire_at is
    /// computed fresh anyway. Accurate enforcement across restarts
    /// would need a per-schedule "fires_today" column we don't have
    /// yet (Phase 8).
    /// </summary>
    private readonly Dictionary<long, (DateOnly Day, int Count)> _dailyFires = new();
    private readonly object _dailyFiresLock = new();

    public RunnerHost(
        IScheduleService schedules,
        IProfileService  profiles,
        IProfileGroupService groups,
        IProfileRunner   runner,
        ILogger<RunnerHost> log)
    {
        _schedules = schedules;
        _profiles  = profiles;
        _groups    = groups;
        _runner    = runner;
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
        // Active-window guard. Outside the schedule's active hours /
        // days → push next_fire_at to a minute later and skip. The
        // next tick will re-evaluate. NOT a failure → no fail_count
        // bump (was a CRITICAL bug pre-fix: 9-5 schedules looked like
        // they were failing all night).
        if (!IsInActiveWindow(s, localNow))
        {
            var nextWindow = ComputeNextFire(s, utcNow.AddMinutes(1));
            await _schedules.RecordDeferralAsync(s.Id, nextWindow, ct);
            _log.LogDebug(
                "Schedule #{Id} '{Name}' outside active window — defer to {Next}",
                s.Id, s.Name, nextWindow);
            return;
        }

        // Daily-fire cap (Simple trigger only). When runs_per_day is
        // set and we've already hit the count for today, defer to the
        // start of tomorrow's active window. The counter is local-time
        // keyed because that's how users think about "150 fires today"
        // — the natural reset is local midnight or active-from hour.
        if (s.TriggerKind == ScheduleTriggerKind.Simple
            && s.RunsPerDay is { } cap and > 0)
        {
            var todayCount = ReadOrResetDailyFires(s.Id, DateOnly.FromDateTime(localNow));
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
            // Cap raced between outer check and StartAsync. Healthy
            // schedule, just bad timing — defer to next tick without
            // touching fail_count.
            var nextSlot = utcNow.Add(TickInterval);
            await _schedules.RecordDeferralAsync(s.Id, nextSlot, ct);
            _log.LogInformation(
                "Schedule #{Id} '{Name}' deferred — cap raced inside fire path",
                s.Id, s.Name);
            return;
        }

        if (outcome == FireOutcome.Launched)
        {
            // Bump the daily counter for Simple schedules — same
            // local-day key as the cap check so they stay consistent.
            if (s.TriggerKind == ScheduleTriggerKind.Simple)
                IncrementDailyFires(s.Id, DateOnly.FromDateTime(localNow));

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
            // Already running — treat as a no-op success so we don't
            // spam fail_count for a long-running session.
            _log.LogDebug(
                "Schedule fire skipped: profile '{Name}' already active",
                profileName);
            return FireOutcome.Launched;
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
                // Simple = uniform-random gap inside [MinJitterSec, MaxJitterSec].
                // Defaults if the editor saved nulls (shouldn't happen with the
                // current dialog but be defensive): 60s..120s.
                var min = s.MinJitterSec is > 0 ? s.MinJitterSec.Value : 60;
                var max = s.MaxJitterSec is > 0 && s.MaxJitterSec.Value >= min
                    ? s.MaxJitterSec.Value
                    : Math.Max(min, 120);
                var gap = Random.Shared.Next(min, max + 1);
                return referenceUtc.AddSeconds(gap);

                // Note: the runs_per_day cap is enforced at fire-time
                // by the runner loop (count today's fire_count delta
                // since midnight). Computing it here would be wrong —
                // we want the row's next_fire_at to keep advancing so
                // the loop can re-evaluate the cap on each tick.
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

    // ─── Daily-fire counter helpers ───────────────────────────────

    private int ReadOrResetDailyFires(long scheduleId, DateOnly today)
    {
        lock (_dailyFiresLock)
        {
            if (_dailyFires.TryGetValue(scheduleId, out var entry))
            {
                if (entry.Day == today) return entry.Count;
                // Day rolled over — drop the old counter.
                _dailyFires.Remove(scheduleId);
            }
            return 0;
        }
    }

    private void IncrementDailyFires(long scheduleId, DateOnly today)
    {
        lock (_dailyFiresLock)
        {
            if (_dailyFires.TryGetValue(scheduleId, out var entry) && entry.Day == today)
                _dailyFires[scheduleId] = (today, entry.Count + 1);
            else
                _dailyFires[scheduleId] = (today, 1);
        }
    }

    /// <summary>
    /// Compute "tomorrow's window start" in local time for a schedule
    /// that hit its daily cap. Uses ActiveFromHour if set, else 00:00.
    /// Returned DateTime carries Kind=Local so callers can convert to
    /// UTC explicitly — we don't tag here because mixing UTC/Local
    /// with TimeZoneInfo is a known footgun across DST transitions.
    /// </summary>
    private static DateTime ComputeTomorrowStart(Schedule s, DateTime localNow)
    {
        var fromHour = s.ActiveFromHour ?? 0;
        var tomorrow = localNow.Date.AddDays(1).AddHours(fromHour);
        return tomorrow;
    }
}

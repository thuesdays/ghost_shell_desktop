// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// CRUD over the <c>schedules</c> table plus the helpers the runner
/// loop needs to find and update due rows.
/// </summary>
public interface IScheduleService
{
    Task<IReadOnlyList<Schedule>> ListAsync(CancellationToken ct = default);
    Task<Schedule?> GetAsync(long id, CancellationToken ct = default);
    Task<Schedule>  CreateAsync(Schedule s, CancellationToken ct = default);
    Task            UpdateAsync(Schedule s, CancellationToken ct = default);
    Task            DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Set <c>enabled</c> in one round-trip.</summary>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// All enabled rows whose <c>next_fire_at</c> is at or before
    /// <paramref name="now"/>. Used by the scheduler tick.
    /// </summary>
    Task<IReadOnlyList<Schedule>> GetDueAsync(DateTime now, CancellationToken ct = default);

    /// <summary>
    /// Update <c>last_fired_at</c>, <c>next_fire_at</c> and bump
    /// <c>fire_count</c>. Called after a successful fire.
    /// </summary>
    Task RecordFiredAsync(long id, DateTime firedAt, DateTime? nextFireAt, CancellationToken ct = default);

    /// <summary>Bump <c>fail_count</c> and roll <c>next_fire_at</c>
    /// forward; called when a fire fails (target missing, runner
    /// rejected, etc).</summary>
    Task RecordFailureAsync(long id, DateTime nextFireAt, CancellationToken ct = default);

    /// <summary>
    /// Roll <c>next_fire_at</c> forward without touching
    /// <c>fail_count</c> — used when a schedule is *deferred* (outside
    /// active window, runner cap reached). Conflating deferrals with
    /// real failures inflates the exponential back-off curve and
    /// makes a properly-configured 9-5 schedule appear to be
    /// "consistently failing" overnight.
    /// </summary>
    Task RecordDeferralAsync(long id, DateTime nextFireAt, CancellationToken ct = default);

    /// <summary>
    /// Phase 71cc — bump <c>fires_today</c> by one (or reset to 1 if
    /// <c>last_fire_day</c> doesn't match <paramref name="localDay"/>),
    /// and stamp <c>last_fire_day</c>. Persistent counter so the
    /// runs-per-day cap survives app restarts. Returns the new count.
    /// </summary>
    Task<int> IncrementFiresTodayAsync(long id, DateOnly localDay, CancellationToken ct = default);

    /// <summary>
    /// Phase 71cc — read the persistent daily counter, automatically
    /// resetting to 0 if <c>last_fire_day</c> is stale. Does NOT
    /// touch the DB on the no-mutation path; only writes when the
    /// counter actually needs to be reset.
    /// </summary>
    Task<int> GetFiresTodayAsync(long id, DateOnly localDay, CancellationToken ct = default);
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 28 — Traffic accounting facade.
///
/// Writers
///   • <see cref="WriteSamplesAsync"/> — UPSERT a batch of deltas the
///     collector built up over its 30-second flush window.
///
/// Readers (range = "last N hours", clamped to [1, 24*90])
///   • <see cref="GetSummaryAsync"/>     — totals + timeseries
///   • <see cref="GetByProfileAsync"/>   — per-profile aggregates
///   • <see cref="GetByDomainAsync"/>    — per-domain aggregates (top N)
///   • <see cref="GetTimeseriesAsync"/>  — single-series timeseries
///                                          (optional profile filter)
///
/// Maintenance
///   • <see cref="CleanupOlderThanAsync"/> — delete rows older than N
///     days. Called from app startup once a day.
///   • <see cref="ClearForProfileAsync"/>  — wipe one profile's traffic;
///     called from ProfileService.DeleteAsync's cascade.
/// </summary>
public interface ITrafficService
{
    /// <summary>UPSERT a batch of deltas. Buckets are computed from
    /// each delta's <see cref="TrafficDelta.Timestamp"/> (local time).
    /// Same (profile, domain, bucket) inside the batch are pre-merged
    /// before the SQL upsert, so we issue at most one row per unique
    /// triple per call.</summary>
    Task WriteSamplesAsync(IEnumerable<TrafficDelta> deltas, CancellationToken ct = default);

    Task<TrafficSummary> GetSummaryAsync(int hours, string? bucket = null, CancellationToken ct = default);

    Task<IReadOnlyList<TrafficByProfile>> GetByProfileAsync(int hours, CancellationToken ct = default);

    Task<IReadOnlyList<TrafficByDomain>> GetByDomainAsync(
        int hours, int limit = 50, string? profileName = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<TrafficTimePoint>> GetTimeseriesAsync(
        int hours, string bucket = "hour", string? profileName = null,
        CancellationToken ct = default);

    Task CleanupOlderThanAsync(int days, CancellationToken ct = default);

    Task ClearForProfileAsync(string profileName, CancellationToken ct = default);
}

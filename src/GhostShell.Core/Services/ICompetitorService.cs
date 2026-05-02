// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 34 — competitor observation log + analytics for the
/// Competitors page.
///
/// The script runner inserts one row per ad slot via
/// <see cref="RecordAsync"/>. The Competitors page reads back via the
/// other methods — KPI tiles, line chart, leaderboard.
///
/// All time-window queries take a <c>days</c> parameter; passing
/// <c>0</c> means "all-time" (used by the "All" period button).
/// </summary>
public interface ICompetitorService
{
    /// <summary>Insert one row.</summary>
    Task<long> RecordAsync(CompetitorRecord row, CancellationToken ct = default);

    /// <summary>Bulk insert — used by the script runner to flush a
    /// page of ads at once. Returns the number inserted.</summary>
    Task<int> RecordBatchAsync(IReadOnlyCollection<CompetitorRecord> rows, CancellationToken ct = default);

    /// <summary>KPI tile counters for the current period.</summary>
    Task<CompetitorKpis> GetKpisAsync(int days, CancellationToken ct = default);

    /// <summary>Top N domains by mentions in the current period vs.
    /// the previous period (for the trend arrow + "new" badge).
    /// Optional <paramref name="search"/> filters by domain
    /// substring (matches the page's search box).</summary>
    Task<IReadOnlyList<CompetitorLeaderRow>> GetLeaderboardAsync(int days, string? search = null, int top = 100, CancellationToken ct = default);

    /// <summary>Daily-bucket line-chart data for the top N domains.</summary>
    Task<IReadOnlyList<CompetitorTrendPoint>> GetTrendAsync(int days, int top = 8, CancellationToken ct = default);

    /// <summary>Recent-ads tab — last N ad rows in time order.</summary>
    Task<IReadOnlyList<CompetitorRecord>> GetRecentAsync(int days, int limit = 200, CancellationToken ct = default);

    /// <summary>By-query tab — top N queries by ad count.</summary>
    Task<IReadOnlyList<CompetitorByQueryRow>> GetByQueryAsync(int days, int top = 50, CancellationToken ct = default);

    /// <summary>Permanently delete records older than
    /// <paramref name="keepDays"/>. Returns the number deleted.</summary>
    Task<int> PurgeOlderThanAsync(int keepDays, CancellationToken ct = default);
}

/// <summary>KPI tile counters for the Competitors page.</summary>
public sealed record CompetitorKpis
{
    public int Records       { get; init; }   // total rows in period
    public int UniqueDomains { get; init; }   // distinct domains in period
    public int NewDomains    { get; init; }   // first-seen-in-period count
    public int ActiveDomains { get; init; }   // seen in last 3 days
    public int QuietingDomains { get; init; } // not seen in 14+ days but seen earlier
}

/// <summary>One row on the By-query tab.</summary>
public sealed record CompetitorByQueryRow
{
    public required string Query { get; init; }
    public int     AdCount      { get; init; }   // total ads observed for this query
    public int     UniqueDomains{ get; init; }
    public DateTime LastSeen    { get; init; }
}

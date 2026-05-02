// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// Phase 34 — Ad density trend service. Mirrors the legacy web project's
/// /api/metrics/ad-density endpoint, reading from <c>runs</c> and
/// <c>action_events</c> to compute ads-per-query KPIs, CTR proxy, and
/// daily/profile/IP breakdowns over 7-day and 14-day windows.
/// </summary>
internal sealed class AdDensityService : IAdDensityService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<AdDensityService> _log;

    public AdDensityService(DatabaseConnection db, ILogger<AdDensityService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<AdDensitySummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var d7 = (now - TimeSpan.FromDays(7)).ToString("O");
        var d14 = (now - TimeSpan.FromDays(14)).ToString("O");
        var d1 = (now - TimeSpan.FromDays(1)).ToString("O");
        var d8 = (now - TimeSpan.FromDays(8)).ToString("O");

        var result = await _db.QueueAsync(async c =>
        {
            // Fetch 7d and 1d averages for the main KPIs
            var stats7d = await c.QuerySingleOrDefaultAsync<(long? Ads, long? Queries, int? Runs)>(
                """
                SELECT
                  COALESCE(SUM(total_ads), 0)     AS Ads,
                  COALESCE(SUM(total_queries), 0) AS Queries,
                  COUNT(*)                        AS Runs
                FROM runs
                WHERE started_at >= @d7 AND finished_at IS NOT NULL;
                """, new { d7 });

            var stats1d = await c.QuerySingleOrDefaultAsync<(long? Ads, long? Queries)>(
                """
                SELECT
                  COALESCE(SUM(total_ads), 0)     AS Ads,
                  COALESCE(SUM(total_queries), 0) AS Queries
                FROM runs
                WHERE started_at >= @d1 AND finished_at IS NOT NULL;
                """, new { d1 });

            // Prev 7d window: day 8 to day 14
            var statsPrev7d = await c.QuerySingleOrDefaultAsync<(long? Ads, long? Queries)>(
                """
                SELECT
                  COALESCE(SUM(total_ads), 0)     AS Ads,
                  COALESCE(SUM(total_queries), 0) AS Queries
                FROM runs
                WHERE started_at >= @d8 AND started_at < @d7 AND finished_at IS NOT NULL;
                """, new { d8, d7 });

            var avgAds7d = stats7d.Queries.GetValueOrDefault() == 0
                ? 0.0
                : stats7d.Ads.GetValueOrDefault() / (double)stats7d.Queries.GetValueOrDefault();
            var avgAds1d = stats1d.Queries.GetValueOrDefault() == 0
                ? 0.0
                : stats1d.Ads.GetValueOrDefault() / (double)stats1d.Queries.GetValueOrDefault();
            var avgAdsPrev7d = statsPrev7d.Queries.GetValueOrDefault() == 0
                ? 0.0
                : statsPrev7d.Ads.GetValueOrDefault() / (double)statsPrev7d.Queries.GetValueOrDefault();
            var deltaPct = avgAdsPrev7d == 0
                ? 0.0
                : ((avgAds7d - avgAdsPrev7d) / avgAdsPrev7d) * 100;

            // CTR: clicks / ads_seen
            var clickCount = await c.QuerySingleOrDefaultAsync<long?>(
                """
                SELECT COALESCE(COUNT(*), 0)
                FROM action_events
                WHERE action_type = 'click_ad' AND outcome = 'ran' AND captured_at >= @d7;
                """, new { d7 });

            var ctr7d = stats7d.Ads.GetValueOrDefault() == 0
                ? 0.0
                : clickCount.GetValueOrDefault() / (double)stats7d.Ads.GetValueOrDefault();

            // Daily breakdown (14 days, fill missing with zeros)
            var dailyRaw = await c.QueryAsync<(string Date, long Ads, long Queries, int Runs)>(
                """
                SELECT
                  substr(started_at, 1, 10) AS Date,
                  COALESCE(SUM(total_ads), 0)     AS Ads,
                  COALESCE(SUM(total_queries), 0) AS Queries,
                  COUNT(*)                        AS Runs
                FROM runs
                WHERE started_at >= @d14 AND finished_at IS NOT NULL
                GROUP BY substr(started_at, 1, 10)
                ORDER BY Date ASC;
                """, new { d14 });

            var dailyList = FillDailyBreakdown(dailyRaw.ToList(), 14);

            // Top 10 profiles by run count
            var topProfiles = await c.QueryAsync<(string ProfileName, int Runs, double AdsPerQuery)>(
                """
                SELECT
                  profile_name       AS ProfileName,
                  COUNT(*)           AS Runs,
                  SUM(total_ads) * 1.0 / NULLIF(SUM(total_queries), 0) AS AdsPerQuery
                FROM runs
                WHERE started_at >= @d7 AND finished_at IS NOT NULL
                GROUP BY profile_name
                ORDER BY Runs DESC
                LIMIT 10;
                """, new { d7 });

            // Top 10 IPs by run count (only if ip_used is not null)
            var topIps = await c.QueryAsync<(string Ip, int Runs, double AdsPerQuery)>(
                """
                SELECT
                  ip_used         AS Ip,
                  COUNT(*)        AS Runs,
                  SUM(total_ads) * 1.0 / NULLIF(SUM(total_queries), 0) AS AdsPerQuery
                FROM runs
                WHERE ip_used IS NOT NULL AND started_at >= @d7 AND finished_at IS NOT NULL
                GROUP BY ip_used
                ORDER BY Runs DESC
                LIMIT 10;
                """, new { d7 });

            return new AdDensitySummary
            {
                AvgAdsPerQuery7d = Math.Round(avgAds7d, 2),
                AvgAdsPerQuery24h = Math.Round(avgAds1d, 2),
                DeltaPct7dPrev = Math.Round(deltaPct, 2),
                TotalRuns7d = (int)(stats7d.Runs ?? 0),
                TotalAds7d = (int)(stats7d.Ads ?? 0),
                TotalQueries7d = (int)(stats7d.Queries ?? 0),
                TotalClicks7d = (int)(clickCount ?? 0),
                Ctr7d = Math.Round(ctr7d, 4),
                Daily = dailyList,
                PerProfile = topProfiles
                    .Select(x => new AdDensityProfileRow
                    {
                        ProfileName = x.ProfileName,
                        Runs = x.Runs,
                        AdsPerQuery = double.IsNaN(x.AdsPerQuery) ? 0.0 : Math.Round(x.AdsPerQuery, 2),
                    })
                    .ToList(),
                PerIp = topIps
                    .Select(x => new AdDensityIpRow
                    {
                        Ip = x.Ip,
                        Runs = x.Runs,
                        AdsPerQuery = double.IsNaN(x.AdsPerQuery) ? 0.0 : Math.Round(x.AdsPerQuery, 2),
                    })
                    .ToList(),
            };
        }, ct);

        return result;
    }

    public Task RecordActionAsync(ActionEvent ev, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO action_events
              (run_id, profile_name, captured_at, query, ad_domain, ad_class, action_type, outcome, skip_reason, duration_sec, error)
            VALUES
              (@runId, @profileName, @capturedAt, @query, @adDomain, @adClass, @actionType, @outcome, @skipReason, @durationSec, @error);
            """;

        return _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            runId = ev.RunId,
            profileName = ev.ProfileName,
            capturedAt = ev.CapturedAt.ToString("O"),
            query = ev.Query,
            adDomain = ev.AdDomain,
            adClass = ev.AdClass,
            actionType = ev.ActionType,
            outcome = ev.Outcome,
            skipReason = ev.SkipReason,
            durationSec = ev.DurationSec,
            error = ev.Error,
        }), ct);
    }

    /// <summary>
    /// Fill missing days in the 14-day breakdown so the UI sparkline
    /// has a stable x-axis. Returns DateOnly + raw counts + computed aps.
    /// </summary>
    private static IReadOnlyList<AdDensityDailyPoint> FillDailyBreakdown(
        List<(string Date, long Ads, long Queries, int Runs)> rows, int days)
    {
        var now = DateTime.UtcNow;
        var start = now.AddDays(-days + 1).Date;
        var map = rows.ToDictionary(
            r => r.Date,
            r => (r.Ads, r.Queries, r.Runs));

        var result = new List<AdDensityDailyPoint>();
        for (int i = 0; i < days; i++)
        {
            var date = start.AddDays(i);
            var dateStr = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var (ads, queries, runs) = map.TryGetValue(dateStr, out var entry)
                ? (entry.Ads, entry.Queries, entry.Runs)
                : (0L, 0L, 0);

            var aps = queries == 0 ? 0.0 : ads / (double)queries;
            result.Add(new AdDensityDailyPoint
            {
                Date = DateOnly.FromDateTime(date),
                Runs = runs,
                Ads = (int)ads,
                Queries = (int)queries,
                AdsPerQuery = Math.Round(aps, 2),
            });
        }

        return result;
    }
}

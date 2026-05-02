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
/// Phase 28 — Traffic service. UPSERT writer + indexed range readers.
/// Mirrors the legacy web project's traffic_stats handlers in shape +
/// query plan. Hour buckets are LOCAL time so the UI shows "8 PM" the
/// same hour the user observed it; ranges are computed in local time
/// too.
/// </summary>
internal sealed class TrafficService : ITrafficService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<TrafficService> _log;

    public TrafficService(DatabaseConnection db, ILogger<TrafficService> log)
    {
        _db  = db;
        _log = log;
    }

    public static string BucketHour(DateTime localTimestamp)
        => localTimestamp.ToString("yyyy-MM-dd HH", CultureInfo.InvariantCulture);
    public static string BucketDay(DateTime localTimestamp)
        => localTimestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Pre-merge a batch of deltas so we issue at most one
    /// UPSERT per (profile, domain, hour bucket). Static + pure so the
    /// unit tests can exercise the math without touching SQLite.</summary>
    public static IReadOnlyDictionary<(string Profile, string Domain, string Bucket), (long Bytes, long Requests, long? RunId)>
        MergeDeltas(IEnumerable<TrafficDelta> deltas)
    {
        var merged = new Dictionary<(string p, string d, string h), (long b, long r, long? run)>();
        if (deltas is null) return merged;
        foreach (var d in deltas)
        {
            if (string.IsNullOrWhiteSpace(d.ProfileName) ||
                string.IsNullOrWhiteSpace(d.Domain)) continue;
            var bucket = BucketHour(d.Timestamp);
            var key = (d.ProfileName, d.Domain, bucket);
            if (merged.TryGetValue(key, out var existing))
            {
                merged[key] = (existing.b + d.Bytes,
                               existing.r + d.ReqCount,
                               existing.run ?? d.RunId);
            }
            else
            {
                merged[key] = (d.Bytes, d.ReqCount, d.RunId);
            }
        }
        return merged;
    }

    public async Task WriteSamplesAsync(
        IEnumerable<TrafficDelta> deltas, CancellationToken ct = default)
    {
        if (deltas is null) return;
        var merged = MergeDeltas(deltas);
        if (merged.Count == 0) return;
        const string sql = """
            INSERT INTO traffic_stats
              (profile_name, domain, hour_bucket, bytes, req_count, run_id, updated_at)
            VALUES
              (@profile, @domain, @bucket, @bytes, @req, @run, @updated)
            ON CONFLICT(profile_name, domain, hour_bucket)
            DO UPDATE SET
              bytes      = bytes      + excluded.bytes,
              req_count  = req_count  + excluded.req_count,
              run_id     = COALESCE(run_id, excluded.run_id),
              updated_at = excluded.updated_at;
        """;
        var nowIso = DateTime.UtcNow.ToString("O");
        await _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var ((profile, domain, bucket), (bytes, req, run)) in merged)
            {
                await c.ExecuteAsync(sql, new
                {
                    profile, domain, bucket, bytes, req, run, updated = nowIso,
                }, tx);
            }
            tx.Commit();
            return 0;
        }, ct);
    }

    // ─── Readers ──────────────────────────────────────────────────────

    private static int ClampHours(int hours) => Math.Clamp(hours, 1, 24 * 90);
    private static int ClampLimit(int limit) => Math.Clamp(limit, 1, 500);

    /// <summary>Lower bound on the bucket column for a "last N hours"
    /// window. Returns the local-time hour-bucket label N hours ago.</summary>
    private static string LowerBound(int hours)
        => BucketHour(DateTime.Now - TimeSpan.FromHours(ClampHours(hours)));

    public async Task<TrafficSummary> GetSummaryAsync(
        int hours, string? bucket = null, CancellationToken ct = default)
    {
        hours = ClampHours(hours);
        var lo = LowerBound(hours);
        // Totals.
        var totals = await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<(long? Bytes, long? Reqs, int? Profiles, int? Domains)>(
            """
            SELECT
              COALESCE(SUM(bytes), 0)                  AS Bytes,
              COALESCE(SUM(req_count), 0)              AS Reqs,
              COUNT(DISTINCT profile_name)             AS Profiles,
              COUNT(DISTINCT domain)                   AS Domains
            FROM traffic_stats
            WHERE hour_bucket >= @lo;
            """, new { lo }), ct);
        var series = await GetTimeseriesAsync(hours, bucket ?? AutoBucket(hours), null, ct);
        return new TrafficSummary
        {
            TotalBytes    = totals.Bytes ?? 0,
            TotalRequests = totals.Reqs  ?? 0,
            ProfileCount  = totals.Profiles ?? 0,
            DomainCount   = totals.Domains  ?? 0,
            Timeseries    = series,
        };
    }

    public async Task<IReadOnlyList<TrafficByProfile>> GetByProfileAsync(
        int hours, CancellationToken ct = default)
    {
        hours = ClampHours(hours);
        var lo = LowerBound(hours);
        const string sql = """
            SELECT
              profile_name AS ProfileName,
              SUM(bytes)     AS Bytes,
              SUM(req_count) AS Requests,
              COUNT(DISTINCT domain) AS DomainCount
            FROM traffic_stats
            WHERE hour_bucket >= @lo
            GROUP BY profile_name
            ORDER BY Bytes DESC;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<TrafficByProfile>(sql, new { lo }), ct);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TrafficByDomain>> GetByDomainAsync(
        int hours, int limit = 50, string? profileName = null,
        CancellationToken ct = default)
    {
        hours = ClampHours(hours);
        limit = ClampLimit(limit);
        var lo = LowerBound(hours);
        var args = new DynamicParameters();
        args.Add("lo", lo);
        args.Add("lim", limit);
        var sql = string.IsNullOrWhiteSpace(profileName)
            ? """
              SELECT
                domain        AS Domain,
                SUM(bytes)      AS Bytes,
                SUM(req_count)  AS Requests,
                COUNT(DISTINCT profile_name) AS ProfileCount
              FROM traffic_stats
              WHERE hour_bucket >= @lo
              GROUP BY domain
              ORDER BY Bytes DESC
              LIMIT @lim;
              """
            : """
              SELECT
                domain        AS Domain,
                SUM(bytes)      AS Bytes,
                SUM(req_count)  AS Requests,
                1               AS ProfileCount
              FROM traffic_stats
              WHERE hour_bucket >= @lo AND profile_name = @profile
              GROUP BY domain
              ORDER BY Bytes DESC
              LIMIT @lim;
              """;
        if (!string.IsNullOrWhiteSpace(profileName)) args.Add("profile", profileName);
        var rows = await _db.QueueAsync(c => c.QueryAsync<TrafficByDomain>(sql, args), ct);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<TrafficTimePoint>> GetTimeseriesAsync(
        int hours, string bucket = "hour", string? profileName = null,
        CancellationToken ct = default)
    {
        hours = ClampHours(hours);
        var lo = LowerBound(hours);
        var groupExpr = string.Equals(bucket, "day", StringComparison.OrdinalIgnoreCase)
            ? "substr(hour_bucket, 1, 10)" // 'YYYY-MM-DD'
            : "hour_bucket";               // 'YYYY-MM-DD HH'
        var args = new DynamicParameters();
        args.Add("lo", lo);
        var sql = $"""
            SELECT
              {groupExpr} AS Time,
              SUM(bytes)     AS Bytes,
              SUM(req_count) AS Requests
            FROM traffic_stats
            WHERE hour_bucket >= @lo
            {(string.IsNullOrWhiteSpace(profileName) ? "" : "AND profile_name = @profile")}
            GROUP BY {groupExpr}
            ORDER BY Time ASC;
        """;
        if (!string.IsNullOrWhiteSpace(profileName)) args.Add("profile", profileName);
        var rows = await _db.QueueAsync(c => c.QueryAsync<TrafficTimePoint>(sql, args), ct);
        return rows.ToList();
    }

    public Task CleanupOlderThanAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var cutoff = BucketHour(DateTime.Now - TimeSpan.FromDays(days));
        return _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM traffic_stats WHERE hour_bucket < @cutoff;",
            new { cutoff }), ct);
    }

    public Task ClearForProfileAsync(string profileName, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM traffic_stats WHERE profile_name = @profile;",
            new { profile = profileName }), ct);

    /// <summary>Hour buckets up to 48 hours; day buckets above. Matches
    /// the legacy web's traffic.js bucket-selection rule so chart
    /// resolution stays in the 24-90 point sweet spot.</summary>
    public static string AutoBucket(int hours) => hours <= 48 ? "hour" : "day";
}

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
/// Phase 34 — Competitor observation log + analytics. Script runner
/// inserts competitor ads via <see cref="RecordAsync"/>; the Competitors
/// page reads back KPIs, leaderboard, trend, and recent/by-query tabs.
/// </summary>
internal sealed class CompetitorService : ICompetitorService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<CompetitorService> _log;

    public CompetitorService(DatabaseConnection db, ILogger<CompetitorService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<long> RecordAsync(CompetitorRecord row, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO competitor_records
              (run_id, profile_name, captured_at, query, domain, ad_title, display_url, clean_url, click_url)
            VALUES
              (@runId, @profileName, @capturedAt, @query, @domain, @adTitle, @displayUrl, @cleanUrl, @clickUrl);
            """;
        var capturedAt = row.CapturedAt.ToString("O");
        // Phase 70 fix — Microsoft.Data.Sqlite is CASE-SENSITIVE on parameter
        // names (unlike SqlClient). The SQL uses @runId / @profileName (camel-
        // case) but the anonymous-object shorthand `new { row.RunId }` projects
        // a property named "RunId" (PascalCase). Mismatch → "Must add values
        // for the following parameters" exception every time. Spell each
        // parameter explicitly so the names match.
        var result = await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            runId       = row.RunId,
            profileName = row.ProfileName,
            capturedAt,
            query       = row.Query,
            domain      = row.Domain,
            adTitle     = row.AdTitle,
            displayUrl  = row.DisplayUrl,
            cleanUrl    = row.CleanUrl,
            clickUrl    = row.ClickUrl,
        }), ct);
        return 1; // scalar insert; could return the last insert rowid via ExecuteScalarAsync if needed
    }

    public async Task<int> RecordBatchAsync(IReadOnlyCollection<CompetitorRecord> rows, CancellationToken ct = default)
    {
        if (rows is null || rows.Count == 0) return 0;
        const string sql = """
            INSERT INTO competitor_records
              (run_id, profile_name, captured_at, query, domain, ad_title, display_url, clean_url, click_url)
            VALUES
              (@runId, @profileName, @capturedAt, @query, @domain, @adTitle, @displayUrl, @cleanUrl, @clickUrl);
            """;
        return await _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();
            foreach (var row in rows)
            {
                var capturedAt = row.CapturedAt.ToString("O");
                // Phase 70 fix — explicit camelCase parameter names so
                // they match the SQL placeholders. See RecordAsync above
                // for the full root-cause comment (SQLite is case-
                // sensitive on parameter names).
                await c.ExecuteAsync(sql, new
                {
                    runId       = row.RunId,
                    profileName = row.ProfileName,
                    capturedAt,
                    query       = row.Query,
                    domain      = row.Domain,
                    adTitle     = row.AdTitle,
                    displayUrl  = row.DisplayUrl,
                    cleanUrl    = row.CleanUrl,
                    clickUrl    = row.ClickUrl,
                }, tx);
            }
            tx.Commit();
            return rows.Count;
        }, ct);
    }

    public async Task<CompetitorKpis> GetKpisAsync(int days, CancellationToken ct = default)
    {
        DateTime? cutoff = days == 0 ? null : DateTime.UtcNow.AddDays(-days);
        var cutoffIso = cutoff?.ToString("O");
        // Two-pass aggregation:
        //   1) per-domain stats across ALL TIME
        //        min_cap   = earliest sighting
        //        max_cap   = most recent sighting
        //        cnt       = total rows
        //        in_period = rows that fall inside the selected period
        //   2) outer SUMs reduce per-domain rows to the page-level KPIs.
        //
        // Earlier version aliased min/max → min_cap/max_cap in the
        // subquery but then re-referenced `captured_at` in the outer
        // SELECT — SQLite returned "no such column: captured_at" because
        // the subquery's projection had renamed it. The shape below
        // uses the aliased names everywhere they're consumed.
        //
        // NewDomains / QuietingDomains need data from BEFORE the period
        // (to decide "first ever" / "last ever"), so the subquery does
        // NOT pre-filter by cutoff. The in_period accumulator picks up
        // the period-restricted bits.
        const string sql = """
            WITH per_domain AS (
              SELECT
                domain,
                MIN(captured_at) AS min_cap,
                MAX(captured_at) AS max_cap,
                COUNT(*)         AS cnt,
                SUM(CASE WHEN @cutoff IS NULL OR captured_at >= @cutoff
                         THEN 1 ELSE 0 END) AS in_period
              FROM competitor_records
              GROUP BY domain
            )
            SELECT
              COALESCE(SUM(in_period), 0)                                                    AS Records,
              SUM(CASE WHEN in_period > 0 THEN 1 ELSE 0 END)                                 AS UniqueDomains,
              SUM(CASE WHEN in_period > 0 AND (@cutoff IS NULL OR min_cap >= @cutoff)
                       THEN 1 ELSE 0 END)                                                    AS NewDomains,
              SUM(CASE WHEN max_cap >= datetime('now', '-3 days')  THEN 1 ELSE 0 END)        AS ActiveDomains,
              SUM(CASE WHEN max_cap <  datetime('now', '-14 days') THEN 1 ELSE 0 END)        AS QuietingDomains
            FROM per_domain;
            """;
        var result = await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<CompetitorKpis>(sql, new { cutoff = cutoffIso }), ct);
        return result ?? new CompetitorKpis();
    }

    public async Task<IReadOnlyList<CompetitorLeaderRow>> GetLeaderboardAsync(
        int days, string? search = null, int top = 100, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 500);
        DateTime? cutoff = days == 0 ? null : DateTime.UtcNow.AddDays(-days);
        var cutoffIso = cutoff?.ToString("O");
        DateTime? prevCutoff = days == 0 ? null : DateTime.UtcNow.AddDays(-2 * days);
        var prevCutoffIso = prevCutoff?.ToString("O");
        var searchLike = string.IsNullOrWhiteSpace(search) ? null : $"%{search.ToLower()}%";

        const string sql = """
            WITH current_period AS (
              SELECT
                domain,
                COUNT(*) AS mentions,
                COUNT(DISTINCT query) AS queriesCount,
                MAX(captured_at) AS lastSeen
              FROM competitor_records
              WHERE (@cutoff IS NULL OR captured_at >= @cutoff)
                AND (@search IS NULL OR LOWER(domain) LIKE @search)
              GROUP BY domain
            ),
            prev_period AS (
              SELECT
                domain,
                COUNT(*) AS mentionsPrev
              FROM competitor_records
              WHERE (@prevCutoff IS NULL OR captured_at >= @prevCutoff)
                AND (@cutoff IS NULL OR captured_at < @cutoff)
              GROUP BY domain
            ),
            all_domains AS (
              SELECT domain, MIN(captured_at) AS firstSeen
              FROM competitor_records
              GROUP BY domain
            ),
            clicks AS (
              SELECT DISTINCT run_id, ad_domain
              FROM action_events
              WHERE action_type = 'click_ad' AND outcome = 'ran'
            )
            SELECT
              c.domain AS Domain,
              c.mentions AS Mentions,
              COALESCE(p.mentionsPrev, 0) AS MentionsPrev,
              c.queriesCount AS QueriesCount,
              COALESCE((SELECT COUNT(DISTINCT c2.run_id) FROM clicks c2 WHERE c2.ad_domain = c.domain), 0) AS ClicksCount,
              c.lastSeen AS LastSeen,
              CASE WHEN COALESCE(p.mentionsPrev, 0) = 0 AND c.mentions > 0 THEN 1 ELSE 0 END AS IsNew
            FROM current_period c
            LEFT JOIN prev_period p ON c.domain = p.domain
            LEFT JOIN all_domains a ON c.domain = a.domain
            ORDER BY c.mentions DESC
            LIMIT @top;
            """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<CompetitorLeaderRow>(sql, new
        {
            cutoff = cutoffIso,
            prevCutoff = prevCutoffIso,
            search = searchLike,
            top,
        }), ct);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<CompetitorTrendPoint>> GetTrendAsync(
        int days, int top = 8, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 100);
        DateTime? cutoff = days == 0 ? null : DateTime.UtcNow.AddDays(-days);
        var cutoffIso = cutoff?.ToString("O");

        const string sql = """
            WITH top_domains AS (
              SELECT domain, COUNT(*) AS mentions
              FROM competitor_records
              WHERE @cutoff IS NULL OR captured_at >= @cutoff
              GROUP BY domain
              ORDER BY mentions DESC
              LIMIT @top
            )
            SELECT
              substr(cr.captured_at, 1, 10) AS Date,
              cr.domain AS Domain,
              COUNT(*) AS Mentions
            FROM competitor_records cr
            WHERE cr.domain IN (SELECT domain FROM top_domains)
              AND (@cutoff IS NULL OR cr.captured_at >= @cutoff)
            GROUP BY substr(cr.captured_at, 1, 10), cr.domain
            ORDER BY cr.domain, Date ASC;
            """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<(string Date, string Domain, int Mentions)>(sql, new
        {
            cutoff = cutoffIso,
            top,
        }), ct);

        var result = new List<CompetitorTrendPoint>();
        foreach (var (dateStr, domain, mentions) in rows)
        {
            if (DateTime.TryParse(dateStr, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
            {
                result.Add(new CompetitorTrendPoint
                {
                    Date = parsedDate,
                    Domain = domain,
                    Mentions = mentions,
                });
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<CompetitorRecord>> GetRecentAsync(
        int days, int limit = 200, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        DateTime? cutoff = days == 0 ? null : DateTime.UtcNow.AddDays(-days);
        var cutoffIso = cutoff?.ToString("O");

        const string sql = """
            SELECT
              id AS Id,
              run_id AS RunId,
              profile_name AS ProfileName,
              captured_at AS CapturedAt,
              query AS Query,
              domain AS Domain,
              ad_title AS AdTitle,
              display_url AS DisplayUrl,
              clean_url AS CleanUrl,
              click_url AS ClickUrl
            FROM competitor_records
            WHERE @cutoff IS NULL OR captured_at >= @cutoff
            ORDER BY captured_at DESC
            LIMIT @limit;
            """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<CompetitorRecord>(sql, new
        {
            cutoff = cutoffIso,
            limit,
        }), ct);
        return rows.ToList();
    }

    public async Task<IReadOnlyList<CompetitorByQueryRow>> GetByQueryAsync(
        int days, int top = 50, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 500);
        DateTime? cutoff = days == 0 ? null : DateTime.UtcNow.AddDays(-days);
        var cutoffIso = cutoff?.ToString("O");

        const string sql = """
            SELECT
              query AS Query,
              COUNT(*) AS AdCount,
              COUNT(DISTINCT domain) AS UniqueDomains,
              MAX(captured_at) AS LastSeen
            FROM competitor_records
            WHERE @cutoff IS NULL OR captured_at >= @cutoff
            GROUP BY query
            ORDER BY AdCount DESC
            LIMIT @top;
            """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<CompetitorByQueryRow>(sql, new
        {
            cutoff = cutoffIso,
            top,
        }), ct);
        return rows.ToList();
    }

    public async Task<int> PurgeOlderThanAsync(int keepDays, CancellationToken ct = default)
    {
        keepDays = Math.Clamp(keepDays, 1, 3650);
        var cutoff = DateTime.UtcNow.AddDays(-keepDays).ToString("O");
        return await _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM competitor_records WHERE captured_at < @cutoff;",
            new { cutoff }), ct);
    }
}

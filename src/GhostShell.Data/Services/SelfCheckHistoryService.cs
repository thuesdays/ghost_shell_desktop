// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

public sealed class SelfCheckHistoryService : ISelfCheckHistoryService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<SelfCheckHistoryService> _log;

    public SelfCheckHistoryService(DatabaseConnection db, ILogger<SelfCheckHistoryService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string SelectColumns = """
        id, profile_name AS ProfileName, run_id AS RunId,
        ran_at AS RanAt, exit_ip AS ExitIp,
        geo_country AS GeoCountry, geo_city AS GeoCity,
        webrtc_leaked AS WebRtcLeaked, webrtc_local_ip AS WebRtcLocalIp,
        timezone_actual AS TimezoneActual, timezone_expected AS TimezoneExpected,
        ua_actual AS UaActual, score, note, raw_json AS RawJson
    """;

    public async Task<long> InsertAsync(SelfCheckResult r, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO selfcheck_results
                (profile_name, run_id, ran_at,
                 exit_ip, geo_country, geo_city,
                 webrtc_leaked, webrtc_local_ip,
                 timezone_actual, timezone_expected,
                 ua_actual, score, note, raw_json)
            VALUES
                (@ProfileName, @RunId, @RanAt,
                 @ExitIp, @GeoCountry, @GeoCity,
                 @WebRtcLeaked, @WebRtcLocalIp,
                 @TimezoneActual, @TimezoneExpected,
                 @UaActual, @Score, @Note, @RawJson)
            RETURNING id;
        """;
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            r.ProfileName,
            r.RunId,
            RanAt    = r.RanAt.ToString("O"),
            r.ExitIp, r.GeoCountry, r.GeoCity,
            WebRtcLeaked = r.WebRtcLeaked ? 1 : 0,
            r.WebRtcLocalIp,
            r.TimezoneActual, r.TimezoneExpected, r.UaActual,
            r.Score, r.Note, r.RawJson,
        }), ct);
        _log.LogInformation(
            "Self-check #{Id} for '{Profile}' score={Score} ip={Ip} tz={Tz} webrtc={Leak}",
            id, r.ProfileName, r.Score, r.ExitIp ?? "?", r.TimezoneActual ?? "?", r.WebRtcLeaked);
        return id;
    }

    public Task<IReadOnlyList<SelfCheckResult>> ListAsync(
        string profileName, int limit = 50, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
              FROM selfcheck_results
             WHERE profile_name = @n
          ORDER BY ran_at DESC
             LIMIT @lim;
        """;
        return _db.QueueAsync<IReadOnlyList<SelfCheckResult>>(async c =>
            (await c.QueryAsync<SelfCheckResult>(sql,
                new { n = profileName, lim = Math.Clamp(limit, 1, 1000) })).ToList(),
            ct);
    }

    public Task<SelfCheckResult?> GetLatestAsync(string profileName, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns}
              FROM selfcheck_results
             WHERE profile_name = @n
          ORDER BY ran_at DESC
             LIMIT 1;
        """;
        return _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<SelfCheckResult>(
            sql, new { n = profileName }), ct);
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

public sealed class SessionService : ISessionService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<SessionService> _log;

    public SessionService(DatabaseConnection db, ILogger<SessionService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string MetadataColumns = """
        id,
        profile_name AS ProfileName,
        created_at   AS CreatedAt,
        run_id       AS RunId,
        trigger,
        cookie_count AS CookieCount,
        domain_count AS DomainCount,
        bytes,
        reason
    """;

    public async Task<long> SaveAsync(
        string profileName, SessionPayload payload, long? runId,
        string trigger, string? reason = null, CancellationToken ct = default)
    {
        var cookiesJson = SessionPayloadJson.SerializeCookies(payload.Cookies);
        var storageJson = SessionPayloadJson.SerializeStorage(payload.Storage);
        var domains     = payload.Cookies
            .Select(c => c.Domain.TrimStart('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var bytes       = cookiesJson.Length + storageJson.Length;

        const string sql = """
            INSERT INTO cookie_snapshots
                (profile_name, created_at, run_id, trigger,
                 cookies_json, storage_json,
                 cookie_count, domain_count, bytes, reason)
            VALUES
                (@ProfileName, @CreatedAt, @RunId, @Trigger,
                 @CookiesJson, @StorageJson,
                 @CookieCount, @DomainCount, @Bytes, @Reason)
            RETURNING id;
        """;

        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            ProfileName  = profileName,
            CreatedAt    = DateTime.UtcNow.ToString("O"),
            RunId        = runId,
            Trigger      = trigger,
            CookiesJson  = cookiesJson,
            StorageJson  = storageJson,
            CookieCount  = payload.Cookies.Count,
            DomainCount  = domains,
            Bytes        = bytes,
            Reason       = reason,
        }), ct);

        _log.LogInformation(
            "Snapshot #{Id} saved for '{Profile}' (cookies={Cookies}, domains={Domains}, " +
            "trigger={Trigger}, run={Run})",
            id, profileName, payload.Cookies.Count, domains, trigger, runId);
        return id;
    }

    public Task<IReadOnlyList<SessionSnapshot>> ListAsync(
        string? profileName = null, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {MetadataColumns}
              FROM cookie_snapshots
             WHERE 1=1
        """;
        var p = new DynamicParameters();
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            sql += " AND profile_name = @ProfileName ";
            p.Add("ProfileName", profileName);
        }
        sql += " ORDER BY created_at DESC LIMIT @Limit; ";
        p.Add("Limit", Math.Clamp(limit, 1, 1000));

        return _db.QueueAsync<IReadOnlyList<SessionSnapshot>>(async c =>
            (await c.QueryAsync<SessionSnapshot>(sql, p)).ToList(), ct);
    }

    public Task<SessionSnapshot?> GetAsync(long id, CancellationToken ct = default) =>
        _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<SessionSnapshot>(
            $"SELECT {MetadataColumns} FROM cookie_snapshots WHERE id = @id;",
            new { id }), ct);

    public async Task<SessionPayload?> GetPayloadAsync(long id, CancellationToken ct = default)
    {
        var row = await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<(string CookiesJson, string StorageJson)?>(
            "SELECT cookies_json AS CookiesJson, storage_json AS StorageJson " +
            "FROM cookie_snapshots WHERE id = @id;",
            new { id }), ct);
        if (row is null) return null;

        return new SessionPayload
        {
            Cookies = SessionPayloadJson.DeserializeCookies(row.Value.CookiesJson),
            Storage = SessionPayloadJson.DeserializeStorage(row.Value.StorageJson),
        };
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var rows = await _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM cookie_snapshots WHERE id = @id;", new { id }), ct);
        if (rows > 0)
            _log.LogInformation("Snapshot #{Id} deleted", id);
    }

    public Task<SessionSnapshot?> GetLatestAsync(
        string profileName, CancellationToken ct = default)
    {
        // Plain interpolated string (not raw) — `ORDER BY` was less
        // indented than the closing `"""` and the C# raw-string
        // parser refuses that combination. Same query semantically.
        var sql = $"SELECT {MetadataColumns} FROM cookie_snapshots " +
                  "WHERE profile_name = @ProfileName " +
                  "ORDER BY created_at DESC LIMIT 1;";
        return _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<SessionSnapshot>(
            sql, new { ProfileName = profileName }), ct);
    }
}

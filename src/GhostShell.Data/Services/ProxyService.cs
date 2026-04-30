// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

internal sealed class ProxyService : IProxyService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ProxyService> _log;

    public ProxyService(DatabaseConnection db, ILogger<ProxyService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string SelectColumns = """
        slug, name, url,
        is_rotating       AS IsRotating,
        rotation_api_url  AS RotationApiUrl,
        rotation_provider AS RotationProvider,
        rotation_api_key  AS RotationApiKey,
        is_default        AS IsDefault,
        notes,
        last_ip           AS LastIp,
        country,
        country_code      AS CountryCode,
        city,
        asn, isp,
        ip_type           AS IpType,
        latency_ms        AS LatencyMs,
        health,
        last_checked_at   AS LastCheckedAt,
        created_at        AS CreatedAt,
        updated_at        AS UpdatedAt
    """;

    public async Task<IReadOnlyList<Proxy>> ListAsync(CancellationToken ct = default)
    {
        // LEFT JOIN against profiles aggregates how many profiles
        // bind to each proxy in a single round-trip — no N+1.
        var sql = $$"""
            SELECT  {{SelectColumns}},
                    (SELECT COUNT(*) FROM profiles
                       WHERE profiles.proxy_slug = proxies.slug) AS ProfileCount
              FROM  proxies
          ORDER BY  is_default DESC,
                    COALESCE(name, slug) COLLATE NOCASE;
        """;
        var rows = await _db.Get().QueryAsync<ProxyRow>(sql);
        return rows.Select(ToModel).ToList();
    }

    public async Task<Proxy?> GetAsync(string slug, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns}, 0 AS ProfileCount FROM proxies WHERE slug = @slug;";
        var row = await _db.Get().QuerySingleOrDefaultAsync<ProxyRow>(sql, new { slug });
        return row is null ? null : ToModel(row);
    }

    public async Task<Proxy?> GetByUrlAsync(string url, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns}, 0 AS ProfileCount FROM proxies WHERE url = @url;";
        var row = await _db.Get().QuerySingleOrDefaultAsync<ProxyRow>(sql, new { url });
        return row is null ? null : ToModel(row);
    }

    public async Task<Proxy> CreateAsync(Proxy proxy, CancellationToken ct = default)
    {
        var conn = _db.Get();
        using var tx = conn.BeginTransaction();
        var inserted = await InsertOneAsync(conn, tx, proxy);
        tx.Commit();

        _log.LogInformation("Created proxy slug='{Slug}' name='{Name}' default={Def} rotating={Rot}",
            inserted.Slug, inserted.Name ?? "—", inserted.IsDefault, inserted.IsRotating);
        return inserted;
    }

    public async Task<BulkCreateResult> BulkCreateAsync(
        IReadOnlyList<Proxy> proxies, CancellationToken ct = default)
    {
        var conn = _db.Get();
        using var tx = conn.BeginTransaction();

        // Pull all existing URLs once so we can dedupe in memory.
        var existing = (await conn.QueryAsync<string>(
            "SELECT url FROM proxies;", transaction: tx)).ToHashSet();

        var created = new List<Proxy>();
        var skipped = new List<string>();

        foreach (var p in proxies)
        {
            if (existing.Contains(p.Url))
            {
                skipped.Add(p.Url);
                continue;
            }
            var inserted = await InsertOneAsync(conn, tx, p);
            existing.Add(inserted.Url);
            created.Add(inserted);
        }
        tx.Commit();

        _log.LogInformation("Bulk import: {Created} created, {Skipped} skipped (duplicates).",
            created.Count, skipped.Count);
        return new BulkCreateResult(created, skipped);
    }

    private static async Task<Proxy> InsertOneAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx,
        Proxy proxy)
    {
        var now = DateTime.UtcNow;
        var row = ToRow(proxy) with
        {
            CreatedAt = proxy.CreatedAt == default ? now : proxy.CreatedAt,
            UpdatedAt = now,
        };

        // Single-default invariant — if this row claims default, clear
        // every existing one first.
        if (row.IsDefault == 1)
        {
            await conn.ExecuteAsync(
                "UPDATE proxies SET is_default = 0 WHERE is_default = 1;",
                transaction: tx);
        }

        const string sql = """
            INSERT INTO proxies
                (slug, name, url, is_rotating, rotation_api_url,
                 rotation_provider, rotation_api_key, is_default,
                 notes, last_ip, country, country_code, city, asn, isp,
                 ip_type, latency_ms, health, last_checked_at,
                 created_at, updated_at)
            VALUES
                (@Slug, @Name, @Url, @IsRotating, @RotationApiUrl,
                 @RotationProvider, @RotationApiKey, @IsDefault,
                 @Notes, @LastIp, @Country, @CountryCode, @City, @Asn, @Isp,
                 @IpType, @LatencyMs, @Health, @LastCheckedAt,
                 @CreatedAt, @UpdatedAt);
        """;
        await conn.ExecuteAsync(sql, row, transaction: tx);
        return ToModel(row);
    }

    public async Task UpdateAsync(Proxy proxy, CancellationToken ct = default)
    {
        var row = ToRow(proxy) with { UpdatedAt = DateTime.UtcNow };

        var conn = _db.Get();
        using var tx = conn.BeginTransaction();

        if (row.IsDefault == 1)
        {
            await conn.ExecuteAsync(
                "UPDATE proxies SET is_default = 0 WHERE is_default = 1 AND slug <> @slug;",
                new { slug = row.Slug }, transaction: tx);
        }

        const string sql = """
            UPDATE proxies
               SET name              = @Name,
                   url               = @Url,
                   is_rotating       = @IsRotating,
                   rotation_api_url  = @RotationApiUrl,
                   rotation_provider = @RotationProvider,
                   rotation_api_key  = @RotationApiKey,
                   is_default        = @IsDefault,
                   notes             = @Notes,
                   updated_at        = @UpdatedAt
             WHERE slug              = @Slug;
        """;
        await conn.ExecuteAsync(sql, row, transaction: tx);
        tx.Commit();

        _log.LogInformation("Updated proxy '{Slug}'", row.Slug);
    }

    public async Task DeleteAsync(string slug, CancellationToken ct = default)
    {
        var rows = await _db.Get().ExecuteAsync(
            "DELETE FROM proxies WHERE slug = @slug;", new { slug });
        _log.LogInformation("Deleted proxy '{Slug}' ({Rows} row(s) affected)", slug, rows);
    }

    public async Task RecordTestResultAsync(
        string slug, ProxyTestResult result, CancellationToken ct = default)
    {
        var health = result.Ok
            ? (result.LatencyMs is > 2000 ? "warning" : "healthy")
            : "critical";

        const string sql = """
            UPDATE proxies
               SET last_ip         = @Ip,
                   country         = @Country,
                   country_code    = @CountryCode,
                   city            = @City,
                   asn             = @Asn,
                   isp             = @Isp,
                   ip_type         = @IpType,
                   latency_ms      = @LatencyMs,
                   health          = @Health,
                   last_checked_at = @At,
                   updated_at      = @At
             WHERE slug            = @Slug;
        """;
        await _db.Get().ExecuteAsync(sql, new
        {
            Slug        = slug,
            Ip          = result.Ip,
            Country     = result.Country,
            CountryCode = result.CountryCode,
            City        = result.City,
            Asn         = result.Asn,
            Isp         = result.Isp,
            IpType      = result.IpType.ToString().ToLowerInvariant(),
            LatencyMs   = result.LatencyMs,
            Health      = health,
            At          = result.At,
        });

        _log.LogDebug("Test result recorded for '{Slug}': ok={Ok} latency={Latency}ms type={Type}",
            slug, result.Ok, result.LatencyMs, result.IpType);
    }

    // ─── Row mapping ───
    private sealed record ProxyRow
    {
        public required string Slug { get; init; }
        public string? Name { get; init; }
        public required string Url { get; init; }
        public int IsRotating { get; init; }
        public string? RotationApiUrl { get; init; }
        public string? RotationProvider { get; init; }
        public string? RotationApiKey { get; init; }
        public int IsDefault { get; init; }
        public string? Notes { get; init; }
        public string? LastIp { get; init; }
        public string? Country { get; init; }
        public string? CountryCode { get; init; }
        public string? City { get; init; }
        public string? Asn { get; init; }
        public string? Isp { get; init; }
        public string IpType { get; init; } = "unknown";
        public int? LatencyMs { get; init; }
        public string Health { get; init; } = "unknown";
        public DateTime? LastCheckedAt { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
        public int ProfileCount { get; init; }
    }

    private static Proxy ToModel(ProxyRow r) => new()
    {
        Slug             = r.Slug,
        Name             = r.Name,
        Url              = r.Url,
        IsRotating       = r.IsRotating == 1,
        RotationApiUrl   = r.RotationApiUrl,
        RotationProvider = r.RotationProvider,
        RotationApiKey   = r.RotationApiKey,
        IsDefault        = r.IsDefault == 1,
        Notes            = r.Notes,
        LastIp           = r.LastIp,
        Country          = r.Country,
        CountryCode      = r.CountryCode,
        City             = r.City,
        Asn              = r.Asn,
        Isp              = r.Isp,
        IpType           = ParseIpType(r.IpType),
        LatencyMs        = r.LatencyMs,
        Health           = ParseHealth(r.Health),
        LastCheckedAt    = r.LastCheckedAt,
        ProfileCount     = r.ProfileCount,
        CreatedAt        = r.CreatedAt,
        UpdatedAt        = r.UpdatedAt,
    };

    private static ProxyRow ToRow(Proxy p) => new()
    {
        Slug             = p.Slug,
        Name             = p.Name,
        Url              = p.Url,
        IsRotating       = p.IsRotating ? 1 : 0,
        RotationApiUrl   = p.RotationApiUrl,
        RotationProvider = p.RotationProvider,
        RotationApiKey   = p.RotationApiKey,
        IsDefault        = p.IsDefault ? 1 : 0,
        Notes            = p.Notes,
        LastIp           = p.LastIp,
        Country          = p.Country,
        CountryCode      = p.CountryCode,
        City             = p.City,
        Asn              = p.Asn,
        Isp              = p.Isp,
        IpType           = p.IpType.ToString().ToLowerInvariant(),
        LatencyMs        = p.LatencyMs,
        Health           = p.Health.ToString().ToLowerInvariant(),
        LastCheckedAt    = p.LastCheckedAt,
        CreatedAt        = p.CreatedAt,
        UpdatedAt        = p.UpdatedAt,
    };

    private static ProxyHealth ParseHealth(string s) => s.ToLowerInvariant() switch
    {
        "healthy"  => ProxyHealth.Healthy,
        "warning"  => ProxyHealth.Warning,
        "critical" => ProxyHealth.Critical,
        _          => ProxyHealth.Unknown,
    };

    private static IpType ParseIpType(string s) => s.ToLowerInvariant() switch
    {
        "datacenter"   => IpType.Datacenter,
        "residential"  => IpType.Residential,
        "mobile"       => IpType.Mobile,
        _              => IpType.Unknown,
    };
}

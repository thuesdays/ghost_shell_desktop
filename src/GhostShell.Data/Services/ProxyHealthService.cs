// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

internal sealed class ProxyHealthService : IProxyHealthService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ProxyHealthService> _log;

    public ProxyHealthService(DatabaseConnection db, ILogger<ProxyHealthService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<IReadOnlyList<ProxyHealthEvent>> ListAsync(
        DateTime? since = null, CancellationToken ct = default)
    {
        var cutoff = since ?? DateTime.UtcNow.AddDays(-7);
        const string sql = """
            SELECT  id,
                    proxy_slug AS ProxySlug,
                    kind, at, detail
              FROM  proxy_health_events
             WHERE  at >= @cutoff
          ORDER BY  at ASC;
        """;
        var rows = await _db.Get().QueryAsync<EventRow>(sql, new { cutoff });
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<ProxyHealthEvent>> ListForProxyAsync(
        string proxySlug, DateTime? since = null, CancellationToken ct = default)
    {
        var cutoff = since ?? DateTime.UtcNow.AddDays(-7);
        const string sql = """
            SELECT  id, proxy_slug AS ProxySlug, kind, at, detail
              FROM  proxy_health_events
             WHERE  proxy_slug = @slug AND at >= @cutoff
          ORDER BY  at ASC;
        """;
        var rows = await _db.Get().QueryAsync<EventRow>(sql, new { slug = proxySlug, cutoff });
        return rows.Select(ToModel).ToList();
    }

    public async Task RecordAsync(ProxyHealthEvent ev, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO proxy_health_events (proxy_slug, kind, at, detail)
            VALUES (@ProxySlug, @Kind, @At, @Detail);
        """;
        await _db.Get().ExecuteAsync(sql, new
        {
            ev.ProxySlug,
            Kind   = ev.Kind.ToString().ToLowerInvariant(),
            At     = ev.At == default ? DateTime.UtcNow : ev.At,
            ev.Detail,
        });
        _log.LogDebug("Health event '{Kind}' recorded for '{Slug}'", ev.Kind, ev.ProxySlug);
    }

    public async Task<IReadOnlyDictionary<string, ProxyHealthCounters>> CountersAsync(
        DateTime? since = null, CancellationToken ct = default)
    {
        var cutoff = since ?? DateTime.UtcNow.AddDays(-7);
        const string sql = """
            SELECT  proxy_slug AS ProxySlug,
                    SUM(CASE WHEN kind = 'rotation'   THEN 1 ELSE 0 END) AS Rotations,
                    SUM(CASE WHEN kind = 'captcha'    THEN 1 ELSE 0 END) AS Captchas,
                    SUM(CASE WHEN kind = 'burn'       THEN 1 ELSE 0 END) AS Burns,
                    SUM(CASE WHEN kind = 'firstseen'  THEN 1 ELSE 0 END) AS FirstSeen
              FROM  proxy_health_events
             WHERE  at >= @cutoff
          GROUP BY  proxy_slug;
        """;
        var rows = await _db.Get().QueryAsync<CounterRow>(sql, new { cutoff });
        return rows.ToDictionary(
            r => r.ProxySlug,
            r => new ProxyHealthCounters(r.Rotations, r.Captchas, r.Burns, r.FirstSeen));
    }

    private sealed record EventRow
    {
        public long Id { get; init; }
        public required string ProxySlug { get; init; }
        public required string Kind { get; init; }
        public DateTime At { get; init; }
        public string? Detail { get; init; }
    }

    private sealed record CounterRow
    {
        public required string ProxySlug { get; init; }
        public int Rotations { get; init; }
        public int Captchas { get; init; }
        public int Burns { get; init; }
        public int FirstSeen { get; init; }
    }

    private static ProxyHealthEvent ToModel(EventRow r) => new()
    {
        Id        = r.Id,
        ProxySlug = r.ProxySlug,
        Kind      = ParseKind(r.Kind),
        At        = r.At,
        Detail    = r.Detail,
    };

    private static ProxyHealthEventKind ParseKind(string s) => s.ToLowerInvariant() switch
    {
        "rotation"  => ProxyHealthEventKind.Rotation,
        "captcha"   => ProxyHealthEventKind.Captcha,
        "burn"      => ProxyHealthEventKind.Burn,
        _           => ProxyHealthEventKind.FirstSeen,
    };
}

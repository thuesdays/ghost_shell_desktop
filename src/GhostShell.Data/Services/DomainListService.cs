// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// Phase 34 — Domain-list management (my / target / block). Three-list
/// registry used by the Domains page and the script engine for membership
/// queries. All domains are normalised on write via
/// <see cref="IDomainListService.Normalize"/>.
/// </summary>
internal sealed class DomainListService : IDomainListService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<DomainListService> _log;

    public DomainListService(DatabaseConnection db, ILogger<DomainListService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<IReadOnlyList<DomainListEntry>> ListAsync(
        DomainListKind kind, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
              id        AS Id,
              kind      AS KindStr,
              domain    AS Domain,
              note      AS Note,
              created_at AS CreatedAt
            FROM domain_lists
            WHERE kind = @kindStr
            ORDER BY domain ASC;
        """;
        var kindStr = KindToString(kind);
        var rows = await _db.QueueAsync(c => c.QueryAsync<(long Id, string KindStr, string Domain, string? Note, string CreatedAt)>(
            sql, new { kindStr }), ct);

        return rows.Select(r => new DomainListEntry
        {
            Id        = r.Id,
            Kind      = kind,
            Domain    = r.Domain,
            Note      = r.Note,
            CreatedAt = DateTime.Parse(r.CreatedAt),
        }).ToList();
    }

    public async Task<IReadOnlyList<DomainListEntry>> ListAllAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
              id        AS Id,
              kind      AS KindStr,
              domain    AS Domain,
              note      AS Note,
              created_at AS CreatedAt
            FROM domain_lists
            ORDER BY domain ASC;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<(long Id, string KindStr, string Domain, string? Note, string CreatedAt)>(sql), ct);

        return rows.Select(r => new DomainListEntry
        {
            Id        = r.Id,
            Kind      = StringToKind(r.KindStr),
            Domain    = r.Domain,
            Note      = r.Note,
            CreatedAt = DateTime.Parse(r.CreatedAt),
        }).ToList();
    }

    public async Task ReplaceAsync(
        DomainListKind kind, IEnumerable<string> domains, CancellationToken ct = default)
    {
        // Normalise, deduplicate, filter comments and blanks
        var newDomains = domains
            .Where(d => !string.IsNullOrWhiteSpace(d) && !d.TrimStart().StartsWith("#"))
            .Select(IDomainListService.Normalize)
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (newDomains.Count == 0)
        {
            // Empty list: delete all for this kind
            await _db.QueueAsync(c => c.ExecuteAsync(
                "DELETE FROM domain_lists WHERE kind = @kind;",
                new { kind = KindToString(kind) }), ct);
            _log.LogInformation(
                "Replaced domain list {Kind} with 0 domains", kind);
            return;
        }

        var kindStr = KindToString(kind);
        var nowIso = DateTime.UtcNow.ToString("O");

        // Atomic transaction: insert new, delete removed.
        await _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();

            // INSERT-OR-IGNORE new domains (respects unique index)
            const string insertSql = """
                INSERT OR IGNORE INTO domain_lists (kind, domain, created_at)
                VALUES (@kind, @domain, @created);
            """;
            foreach (var domain in newDomains)
            {
                await c.ExecuteAsync(insertSql, new { kind = kindStr, domain, created = nowIso }, tx);
            }

            // DELETE domains not in the new set. Building a NOT IN clause
            // with parameterised lists is awkward in plain Dapper, so we
            // diff in-memory and issue per-row deletes inside the same
            // transaction — the list is short (typically <100 entries)
            // so the round-trip cost is irrelevant.
            var existingSql = "SELECT domain FROM domain_lists WHERE kind = @kind;";
            var existing = await c.QueryAsync<string>(existingSql, new { kind = kindStr }, tx);
            var toDelete = existing.Where(d => !newDomains.Contains(d, StringComparer.OrdinalIgnoreCase));
            foreach (var domain in toDelete)
            {
                await c.ExecuteAsync(
                    "DELETE FROM domain_lists WHERE kind = @kind AND domain = @domain;",
                    new { kind = kindStr, domain }, tx);
            }

            tx.Commit();
            return 0;
        }, ct);

        _log.LogInformation(
            "Replaced domain list {Kind} with {Count} domains", kind, newDomains.Count);
    }

    public async Task<bool> AddAsync(
        DomainListKind kind, string domain, string? note = null, CancellationToken ct = default)
    {
        domain = IDomainListService.Normalize(domain);
        if (string.IsNullOrEmpty(domain)) return false;

        const string sql = """
            INSERT OR IGNORE INTO domain_lists (kind, domain, note, created_at)
            VALUES (@kind, @domain, @note, @created);
        """;
        var kindStr = KindToString(kind);
        var now = DateTime.UtcNow.ToString("O");

        var count = await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            kind = kindStr,
            domain,
            note,
            created = now,
        }), ct);

        if (count > 0)
        {
            _log.LogDebug("Added domain {Domain} to list {Kind}", domain, kind);
            return true;
        }
        return false;
    }

    public async Task<bool> RemoveAsync(
        DomainListKind kind, string domain, CancellationToken ct = default)
    {
        domain = IDomainListService.Normalize(domain);
        if (string.IsNullOrEmpty(domain)) return false;

        const string sql = "DELETE FROM domain_lists WHERE kind = @kind AND domain = @domain;";
        var kindStr = KindToString(kind);

        var count = await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            kind = kindStr,
            domain,
        }), ct);

        if (count > 0)
        {
            _log.LogDebug("Removed domain {Domain} from list {Kind}", domain, kind);
            return true;
        }
        return false;
    }

    public async Task<DomainListEntry?> IsMatchAsync(
        DomainListKind kind, string? adDomain, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(adDomain)) return null;

        adDomain = IDomainListService.Normalize(adDomain);
        if (string.IsNullOrEmpty(adDomain)) return null;

        // Fetch all entries for this kind and check suffix match in-memory.
        // (Suffix matching is simpler in C# than a SQL pattern, and we expect
        // small lists.)
        var entries = await ListAsync(kind, ct);
        foreach (var entry in entries)
        {
            // Exact match or subdomain: adDomain == entry OR adDomain ends with ".entry"
            if (string.Equals(adDomain, entry.Domain, StringComparison.OrdinalIgnoreCase) ||
                adDomain.EndsWith("." + entry.Domain, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    private static string KindToString(DomainListKind kind) => kind switch
    {
        DomainListKind.My     => "my",
        DomainListKind.Target => "target",
        DomainListKind.Block  => "block",
        _ => throw new ArgumentException($"Unknown kind: {kind}", nameof(kind)),
    };

    private static DomainListKind StringToKind(string kindStr) => kindStr.ToLowerInvariant() switch
    {
        "my"     => DomainListKind.My,
        "target" => DomainListKind.Target,
        "block"  => DomainListKind.Block,
        _ => throw new ArgumentException($"Unknown kind string: {kindStr}", nameof(kindStr)),
    };
}

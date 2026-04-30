// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IProfileGroupService"/>.
/// Public so the test project can target it directly without the
/// reflection-via-DI workaround.
/// </summary>
public sealed class ProfileGroupService : IProfileGroupService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ProfileGroupService> _log;

    public ProfileGroupService(
        DatabaseConnection db,
        ILogger<ProfileGroupService> log)
    {
        _db  = db;
        _log = log;
    }

    public async Task<IReadOnlyList<ProfileGroup>> ListAsync(CancellationToken ct = default)
    {
        // One query joins counts so the list call doesn't N+1 over
        // group_members. We'd hit this hard if the user has 50+
        // groups with hundreds of members each.
        const string sql = """
            SELECT g.id              AS Id,
                   g.name            AS Name,
                   g.description     AS Description,
                   g.max_parallel    AS MaxParallel,
                   g.created_at      AS CreatedAt,
                   g.updated_at      AS UpdatedAt,
                   COALESCE(m.cnt,0) AS MemberCount
              FROM profile_groups g
         LEFT JOIN (SELECT group_id, COUNT(*) cnt
                      FROM profile_group_members
                  GROUP BY group_id) m ON m.group_id = g.id
          ORDER BY g.name COLLATE NOCASE;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<GroupRow>(sql), ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<ProfileGroup?> GetAsync(long id, CancellationToken ct = default)
    {
        const string headSql = """
            SELECT id, name, description, max_parallel AS MaxParallel,
                   created_at AS CreatedAt, updated_at AS UpdatedAt
              FROM profile_groups WHERE id = @id;
        """;
        const string membersSql = """
            SELECT profile_name FROM profile_group_members
             WHERE group_id = @id ORDER BY profile_name COLLATE NOCASE;
        """;

        return await _db.QueueAsync<ProfileGroup?>(async c =>
        {
            var head = await c.QuerySingleOrDefaultAsync<GroupRow>(headSql, new { id });
            if (head is null) return null;
            var members = (await c.QueryAsync<string>(membersSql, new { id })).ToList();
            return ToModel(head, members);
        }, ct);
    }

    public async Task<ProfileGroup> CreateAsync(
        string name,
        string? description,
        int? maxParallel,
        IReadOnlyList<string> members,
        CancellationToken ct = default)
    {
        const string insertGroup = """
            INSERT INTO profile_groups
                (name, description, max_parallel, created_at, updated_at)
            VALUES
                (@name, @description, @maxParallel, @now, @now);
            SELECT last_insert_rowid();
        """;
        const string insertMember = """
            INSERT OR IGNORE INTO profile_group_members (group_id, profile_name)
            VALUES (@gid, @profileName);
        """;

        return await _db.QueueAsync(async (SqliteConnection c) =>
        {
            using var tx = c.BeginTransaction();
            try
            {
                var now = DateTime.UtcNow;
                var id = await c.ExecuteScalarAsync<long>(insertGroup,
                    new { name, description, maxParallel, now }, tx);

                foreach (var pn in members)
                {
                    if (string.IsNullOrWhiteSpace(pn)) continue;
                    await c.ExecuteAsync(insertMember,
                        new { gid = id, profileName = pn }, tx);
                }
                tx.Commit();

                _log.LogInformation(
                    "Created group #{Id} '{Name}' with {Count} member(s)",
                    id, name, members.Count);

                return new ProfileGroup
                {
                    Id          = id,
                    Name        = name,
                    Description = description,
                    MaxParallel = maxParallel,
                    Members     = members.ToList(),
                    MemberCount = members.Count,
                    CreatedAt   = now,
                    UpdatedAt   = now,
                };
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }, ct);
    }

    public async Task UpdateAsync(
        long id,
        string name,
        string? description,
        int? maxParallel,
        CancellationToken ct = default)
    {
        const string sql = """
            UPDATE profile_groups
               SET name         = @name,
                   description  = @description,
                   max_parallel = @maxParallel,
                   updated_at   = @now
             WHERE id = @id;
        """;
        await _db.QueueAsync(c => c.ExecuteAsync(sql,
            new { id, name, description, maxParallel, now = DateTime.UtcNow }), ct);
        _log.LogInformation("Updated group #{Id} '{Name}'", id, name);
    }

    public async Task SetMembersAsync(
        long id,
        IReadOnlyList<string> members,
        CancellationToken ct = default)
    {
        // Diff against the existing set so we only INSERT what's new
        // and DELETE what's gone — keeps the transaction proportional
        // to the actual change rather than the whole list.
        const string existingSql = """
            SELECT profile_name FROM profile_group_members WHERE group_id = @id;
        """;
        const string insertSql = """
            INSERT OR IGNORE INTO profile_group_members (group_id, profile_name)
            VALUES (@gid, @profileName);
        """;
        const string deleteSql = """
            DELETE FROM profile_group_members
             WHERE group_id = @gid AND profile_name = @profileName;
        """;
        const string touchSql = """
            UPDATE profile_groups SET updated_at = @now WHERE id = @id;
        """;

        await _db.QueueAsync(async (SqliteConnection c) =>
        {
            using var tx = c.BeginTransaction();
            try
            {
                var current = (await c.QueryAsync<string>(existingSql, new { id }, tx))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var desired = members
                    .Where(m => !string.IsNullOrWhiteSpace(m))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var add in desired.Except(current))
                    await c.ExecuteAsync(insertSql,
                        new { gid = id, profileName = add }, tx);
                foreach (var rem in current.Except(desired))
                    await c.ExecuteAsync(deleteSql,
                        new { gid = id, profileName = rem }, tx);

                await c.ExecuteAsync(touchSql,
                    new { id, now = DateTime.UtcNow }, tx);
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }, ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        // FK is set ON DELETE CASCADE, so deleting the parent row
        // sweeps the join-table entries automatically.
        await _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM profile_groups WHERE id = @id;", new { id }), ct);
        _log.LogInformation("Deleted group #{Id}", id);
    }

    // ─── Mapping helpers ─────────────────────────────────────────

    private sealed record GroupRow
    {
        public long Id { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public int? MaxParallel { get; init; }
        public int MemberCount { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private static ProfileGroup ToModel(GroupRow r) => new()
    {
        Id          = r.Id,
        Name        = r.Name,
        Description = r.Description,
        MaxParallel = r.MaxParallel,
        MemberCount = r.MemberCount,
        Members     = Array.Empty<string>(),
        CreatedAt   = r.CreatedAt,
        UpdatedAt   = r.UpdatedAt,
    };

    private static ProfileGroup ToModel(GroupRow r, IReadOnlyList<string> members) => new()
    {
        Id          = r.Id,
        Name        = r.Name,
        Description = r.Description,
        MaxParallel = r.MaxParallel,
        MemberCount = members.Count,
        Members     = members,
        CreatedAt   = r.CreatedAt,
        UpdatedAt   = r.UpdatedAt,
    };
}

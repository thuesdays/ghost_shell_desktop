// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

public sealed class ScriptService : IScriptService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ScriptService> _log;

    public ScriptService(DatabaseConnection db, ILogger<ScriptService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string SelectColumns = """
        id, name, description, steps_json AS StepsJson,
        enabled, is_default AS IsDefault, etag AS ETag,
        created_at AS CreatedAt, updated_at AS UpdatedAt
    """;

    public async Task<IReadOnlyList<Script>> ListAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM scripts ORDER BY name COLLATE NOCASE;";
        var rows = await _db.QueueAsync(c => c.QueryAsync<Script>(sql), ct);
        // Dapper round-trips int 0/1 fine for `enabled` if the model
        // uses bool — Dapper does the conversion. Same for DateTime
        // coming back as Unspecified; consumers tolerate it.
        return rows.ToList();
    }

    public Task<Script?> GetAsync(long id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM scripts WHERE id = @id;";
        return _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<Script>(sql, new { id }), ct);
    }

    public async Task<Script> CreateAsync(Script s, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var etag = Guid.NewGuid().ToString("N");
        const string sql = """
            INSERT INTO scripts
                (name, description, steps_json, enabled, is_default, etag, created_at, updated_at)
            VALUES
                (@Name, @Description, @StepsJson, @Enabled, @IsDefault, @ETag, @CreatedAt, @UpdatedAt)
            RETURNING id;
        """;
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            s.Name,
            s.Description,
            s.StepsJson,
            Enabled   = s.Enabled   ? 1 : 0,
            IsDefault = s.IsDefault ? 1 : 0,
            ETag = etag,
            CreatedAt = now,
            UpdatedAt = now,
        }), ct);
        _log.LogInformation("Script #{Id} '{Name}' created", id, s.Name);
        return s with { Id = id, ETag = etag, CreatedAt = now, UpdatedAt = now };
    }

    public async Task UpdateAsync(Script s, string expectedEtag, CancellationToken ct = default)
    {
        // ETag-conditional UPDATE: SQLite returns rowcount, we treat
        // 0 rows changed as "etag drifted" → tell the caller. The
        // editor surface translates that into a "someone else saved
        // this script while you were editing" toast.
        var newEtag = Guid.NewGuid().ToString("N");
        const string sql = """
            UPDATE scripts SET
                name        = @Name,
                description = @Description,
                steps_json  = @StepsJson,
                enabled     = @Enabled,
                etag        = @NewETag,
                updated_at  = @Now
              WHERE id = @Id AND etag = @ExpectedETag;
        """;
        var rows = await _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            s.Id, s.Name, s.Description, s.StepsJson,
            Enabled = s.Enabled ? 1 : 0,
            NewETag = newEtag, ExpectedETag = expectedEtag,
            Now = DateTime.UtcNow,
        }), ct);
        if (rows == 0)
        {
            throw new InvalidOperationException(
                "Script was modified by another session — reload the editor and re-apply your changes.");
        }
        _log.LogInformation("Script #{Id} updated (etag {Old}→{New})",
            s.Id, expectedEtag.Substring(0, Math.Min(8, expectedEtag.Length)), newEtag[..8]);
    }

    public Task DeleteAsync(long id, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM scripts WHERE id = @id;", new { id }), ct);

    public async Task<long> RecordRunAsync(ScriptRun r, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO script_runs
                (script_id, profile_name, started_at, finished_at, status,
                 steps_executed, steps_failed, ads_clicked, duration_sec,
                 last_error, log_json)
            VALUES
                (@ScriptId, @ProfileName, @StartedAt, @FinishedAt, @Status,
                 @StepsExecuted, @StepsFailed, @AdsClicked, @DurationSec,
                 @LastError, @LogJson)
            RETURNING id;
        """;
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            r.ScriptId, r.ProfileName,
            StartedAt   = r.StartedAt.ToString("O"),
            FinishedAt  = r.FinishedAt?.ToString("O"),
            r.Status, r.StepsExecuted, r.StepsFailed, r.AdsClicked,
            r.DurationSec, r.LastError, r.LogJson,
        }), ct);
        return id;
    }

    public async Task SetDefaultAsync(long id, CancellationToken ct = default)
    {
        // Single transaction: clear all then set one. The unique-on-
        // is_default invariant isn't enforced via SQL UNIQUE because
        // SQLite would also forbid multiple zero-rows; this two-step
        // pattern is what the legacy uses.
        await _db.QueueAsync(async (Microsoft.Data.Sqlite.SqliteConnection c) =>
        {
            using var tx = c.BeginTransaction();
            await c.ExecuteAsync(
                "UPDATE scripts SET is_default = 0 WHERE is_default = 1;",
                transaction: tx);
            if (id > 0)
            {
                await c.ExecuteAsync(
                    "UPDATE scripts SET is_default = 1, updated_at = @now WHERE id = @id;",
                    new { id, now = DateTime.UtcNow }, tx);
            }
            tx.Commit();
        }, ct);
        _log.LogInformation("Default script set to #{Id}", id);
    }

    public Task<Script?> GetDefaultAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM scripts WHERE is_default = 1 LIMIT 1;";
        return _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<Script>(sql), ct);
    }

    public async Task AssignToProfilesAsync(
        long? scriptId, IEnumerable<string> profileNames, CancellationToken ct = default)
    {
        var names = profileNames.ToList();
        if (names.Count == 0) return;
        // SQLite won't bind a List<string> directly to IN — use Dapper's
        // list-expansion magic (the @names placeholder explodes).
        const string sql = "UPDATE profiles SET assigned_script_id = @sid WHERE name IN @names;";
        await _db.QueueAsync(c => c.ExecuteAsync(sql,
            new { sid = scriptId, names }), ct);
        _log.LogInformation("Assigned script #{Id} to {Count} profile(s)",
            scriptId ?? 0, names.Count);
    }

    public Task<IReadOnlyList<ScriptRun>> ListRunsAsync(
        long? scriptId = null, string? profileName = null,
        int limit = 50, CancellationToken ct = default)
    {
        var sql = """
            SELECT id, script_id AS ScriptId, profile_name AS ProfileName,
                   started_at AS StartedAt, finished_at AS FinishedAt, status,
                   steps_executed AS StepsExecuted, steps_failed AS StepsFailed,
                   ads_clicked AS AdsClicked, duration_sec AS DurationSec,
                   last_error AS LastError, log_json AS LogJson
              FROM script_runs
             WHERE 1=1
        """;
        var p = new DynamicParameters();
        if (scriptId is not null)
        {
            sql += " AND script_id = @sid ";
            p.Add("sid", scriptId.Value);
        }
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            sql += " AND profile_name = @pn ";
            p.Add("pn", profileName);
        }
        sql += " ORDER BY started_at DESC LIMIT @lim; ";
        p.Add("lim", Math.Clamp(limit, 1, 1000));

        return _db.QueueAsync<IReadOnlyList<ScriptRun>>(async c =>
            (await c.QueryAsync<ScriptRun>(sql, p)).ToList(), ct);
    }
}

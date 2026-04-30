// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// SQLite-backed persistence for fingerprint regen/noise salts +
/// the <c>fingerprint_audits</c> log. The orchestration class
/// (<c>FingerprintService</c> in GhostShell.Runtime) consumes this
/// through <see cref="IFingerprintAuditService"/> so Runtime stays
/// free of a Data project reference.
/// </summary>
public sealed class FingerprintAuditService : IFingerprintAuditService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<FingerprintAuditService> _log;

    public FingerprintAuditService(DatabaseConnection db, ILogger<FingerprintAuditService> log)
    {
        _db  = db;
        _log = log;
    }

    public Task SetRegenSaltAsync(string profileName, string salt, CancellationToken ct = default) =>
        _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE profiles SET fp_regen_salt = @s, updated_at = @now WHERE name = @n;",
            new { s = salt, now = DateTime.UtcNow, n = profileName }), ct);

    public Task SetNoiseSaltAsync(string profileName, string salt, CancellationToken ct = default) =>
        _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE profiles SET fp_noise_salt = @s, updated_at = @now WHERE name = @n;",
            new { s = salt, now = DateTime.UtcNow, n = profileName }), ct);

    public Task LogAsync(string profileName, int score, string templateId,
                         string? note = null, CancellationToken ct = default) =>
        _db.QueueAsync(c => c.ExecuteAsync(
            """
            INSERT INTO fingerprint_audits
                (profile_name, generated_at, score, template_id, note)
            VALUES
                (@n, @at, @s, @t, @note);
            """,
            new
            {
                n    = profileName,
                at   = DateTime.UtcNow.ToString("O"),
                s    = score,
                t    = templateId,
                note,
            }), ct);

    public Task<IReadOnlyList<FingerprintAuditEntry>> ListAsync(
        string profileName, int limit = 50, CancellationToken ct = default)
    {
        const string sql = """
            SELECT id, profile_name AS ProfileName, generated_at AS GeneratedAt,
                   score AS Score, template_id AS TemplateId, note AS Note
              FROM fingerprint_audits
             WHERE profile_name = @n
          ORDER BY generated_at DESC
             LIMIT @lim;
        """;
        return _db.QueueAsync<IReadOnlyList<FingerprintAuditEntry>>(async c =>
            (await c.QueryAsync<FingerprintAuditEntry>(sql,
                new { n = profileName, lim = Math.Clamp(limit, 1, 1000) })).ToList(),
            ct);
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Extensions;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Database;

/// <summary>
/// Phase 31 hot-fix — one-shot migrator that re-computes every
/// extension's <c>ext_id</c> using the current
/// <see cref="ExtensionManifest.SynthesizeExtId"/> algorithm
/// (UTF-16 LE on Windows; UTF-8 elsewhere — matches Chrome).
///
/// Why we need this on top of a pure-SQL migration:
///   • Earlier builds hashed the unpacked path as UTF-8 on every OS,
///     including Windows. Chrome on Windows hashes the same path as
///     UTF-16 LE because <c>base::FilePath::CharType</c> is wchar_t.
///   • The mismatch meant the IDs we wrote into Default/Preferences
///     under <c>extensions.pinned_actions</c> referred to a phantom
///     extension Chrome never sees, so toolbar pins silently failed.
///   • The fix to <c>SynthesizeExtId</c> only helps NEW installs.
///     Existing rows still hold the bad UTF-8 ID, so this migrator
///     re-parses every install on app boot and rewrites the row when
///     the recomputed ID differs.
///
/// Safe to re-run: a row whose stored ID already matches the recomputed
/// one is skipped. Idempotent.
///
/// Cascade: <c>profile_extensions.extension_id</c> is the integer PK
/// (<c>extensions.id</c>), NOT the <c>ext_id</c> string, so updating
/// <c>extensions.ext_id</c> alone doesn't break the per-profile linkage.
/// </summary>
public sealed class ExtensionIdMigrator
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ExtensionIdMigrator> _log;

    public ExtensionIdMigrator(DatabaseConnection db, ILogger<ExtensionIdMigrator> log)
    {
        _db  = db;
        _log = log;
    }

    private sealed class Row
    {
        public long   Id        { get; init; }
        public string ExtId     { get; init; } = "";
        public string Name      { get; init; } = "";
        public string LocalPath { get; init; } = "";
    }

    /// <summary>Re-parse every extension manifest, rewrite ext_id when
    /// it differs from what the current algorithm would produce. Returns
    /// the number of rows that needed updating.</summary>
    public int Run()
    {
        var conn = _db.Get();
        IReadOnlyList<Row> rows;
        try
        {
            rows = conn.Query<Row>(
                "SELECT id AS Id, ext_id AS ExtId, name AS Name, local_path AS LocalPath FROM extensions;"
            ).ToList();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            // First-run on a fresh DB: extensions table doesn't exist
            // yet (Phase 27 migration hasn't applied). Nothing to do.
            _log.LogDebug(ex, "ExtensionIdMigrator skipped — extensions table not present");
            return 0;
        }

        if (rows.Count == 0) return 0;

        int updated = 0;
        int missingPath = 0;
        foreach (var row in rows)
        {
            string newId;
            try
            {
                if (string.IsNullOrWhiteSpace(row.LocalPath) || !Directory.Exists(row.LocalPath))
                {
                    missingPath++;
                    continue;
                }
                var manifestPath = Path.Combine(row.LocalPath, "manifest.json");
                if (!File.Exists(manifestPath))
                {
                    missingPath++;
                    continue;
                }
                var manifest = ExtensionManifest.ParseFromFile(manifestPath, row.LocalPath);
                newId = manifest.ExtId;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "ExtensionIdMigrator: couldn't re-parse '{Name}' at {Path}",
                    row.Name, row.LocalPath);
                continue;
            }
            if (string.IsNullOrEmpty(newId) ||
                string.Equals(newId, row.ExtId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            try
            {
                // Defence: another row already holds this id (somehow
                // collision after the algorithm change). Skip rather
                // than blow up on the unique index — the user will see
                // both rows + can manually delete the dup.
                var conflict = conn.ExecuteScalar<long>(
                    "SELECT COUNT(1) FROM extensions WHERE ext_id = $newId AND id <> $id;",
                    new { newId, id = row.Id });
                if (conflict > 0)
                {
                    _log.LogWarning(
                        "ExtensionIdMigrator: '{Name}' would collide on new id={New}; leaving as-is",
                        row.Name, newId);
                    continue;
                }
                var updatedAt = DateTime.UtcNow.ToString("O");
                conn.Execute(
                    "UPDATE extensions SET ext_id = $newId, updated_at = $u WHERE id = $id;",
                    new { newId, u = updatedAt, id = row.Id });
                _log.LogInformation(
                    "ExtensionIdMigrator: '{Name}' ext_id rewritten {Old} → {New}",
                    row.Name, row.ExtId, newId);
                updated++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "ExtensionIdMigrator: failed to update '{Name}'", row.Name);
            }
        }

        if (updated == 0 && missingPath == 0)
        {
            _log.LogDebug("ExtensionIdMigrator: {Total} extension(s) checked, all up to date",
                rows.Count);
        }
        else
        {
            _log.LogInformation(
                "ExtensionIdMigrator: {Updated} of {Total} ext_id row(s) rewritten ({Missing} skipped — local_path missing)",
                updated, rows.Count, missingPath);
        }
        return updated;
    }
}

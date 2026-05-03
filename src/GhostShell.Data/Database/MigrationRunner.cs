// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Database;

/// <summary>
/// Tiny linear migration runner. We don't use EF Core / FluentMigrator
/// here — the schema is small and stable, and shipping fewer
/// dependencies in the persistence layer keeps the binary lean.
///
/// Each migration is a (version, sql) pair. We record applied
/// versions in `__schema_version` and never re-run them.
/// </summary>
public sealed class MigrationRunner
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<MigrationRunner> _log;

    public MigrationRunner(DatabaseConnection db, ILogger<MigrationRunner> log)
    {
        _db  = db;
        _log = log;
    }

    private static readonly IReadOnlyList<(int Version, string Sql)> Migrations =
    [
        (1, Migrations_V1.Sql),
        (2, Migrations_V2.Sql),
        (3, Migrations_V3.Sql),
        (4, Migrations_V4.Sql),
        (5, Migrations_V5.Sql),
        (6, Migrations_V6.Sql),
        (7, Migrations_V7.Sql),
        (8, Migrations_V8.Sql),
        (9, Migrations_V9.Sql),
        (10, Migrations_V10.Sql),
        (12, Migrations_V12.Sql),
    ];

    // Phase 37 audit fix #4: All known migration versions including tolerant ones (11, 13-23).
    // Used to detect downgrade scenario where DB schema is newer than binary's knowledge.
    private static readonly int[] KnownVersions = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25 };

    public void Run()
    {
        var conn = _db.Get();

        EnsureVersionTable(conn);

        var applied = LoadAppliedVersions(conn);

        // Phase 37 audit fix #4: Detect downgrade (DB newer than binary).
        // If the DB has schema versions this binary doesn't know about,
        // log a warning but proceed read/write (don't abort).
        if (applied.Count > 0)
        {
            var maxAppliedVersion = applied.Max();
            var maxKnownVersion = KnownVersions.Max();
            if (maxAppliedVersion > maxKnownVersion)
            {
                // CA2017 — one named placeholder per arg. The previous form
                // referenced {Known} twice but only passed maxKnownVersion
                // once. Roslyn's logging analyzer counts ALL occurrences
                // of a placeholder. Rename the second to {Known2} so we
                // can supply it explicitly.
                _log.LogWarning(
                    "Database is at schema version {Applied} but this binary only knows up to {Known}. " +
                    "You may have downgraded — proceeding read/write but DB schema features beyond v{Known2} are inaccessible.",
                    maxAppliedVersion, maxKnownVersion, maxKnownVersion);
            }
        }

        foreach (var (version, sql) in Migrations)
        {
            if (applied.Contains(version)) continue;
            ApplyMigration(conn, version, sql, tolerateDuplicateColumn: false);
        }

        // V11 — uses the tolerant statement-list path. The version
        // gets recorded the same way once all statements pass.
        if (!applied.Contains(11))
        {
            ApplyTolerantStatements(conn, 11, Migrations_V11.Statements);
        }

        // V13 — same pattern (the ALTER TABLE inside isn't idempotent
        // by itself but the runner swallows duplicate-column errors).
        if (!applied.Contains(13))
        {
            ApplyTolerantStatements(conn, 13, Migrations_V13.Statements);
        }

        // V14 — scripts.is_default + runs.script_run_id (Phase 12 iter 6).
        if (!applied.Contains(14))
        {
            ApplyTolerantStatements(conn, 14, Migrations_V14.Statements);
        }

        // V15 — profiles.my_domains + target_domains (Phase 20).
        if (!applied.Contains(15))
        {
            ApplyTolerantStatements(conn, 15, Migrations_V15.Statements);
        }

        // V16 — scripts.layout_mode + nodes_json + edges_json (Phase 21).
        if (!applied.Contains(16))
        {
            ApplyTolerantStatements(conn, 16, Migrations_V16.Statements);
        }

        // V17 — vault_items + vault_config (Phase 24, Credential Vault).
        if (!applied.Contains(17))
        {
            ApplyTolerantStatements(conn, 17, Migrations_V17.Statements);
        }

        // V18 — extensions + profile_extensions + extension_store_cache
        // (Phase 27, Browser Extensions).
        if (!applied.Contains(18))
        {
            ApplyTolerantStatements(conn, 18, Migrations_V18.Statements);
        }

        // V19 — traffic_stats hourly bandwidth accounting (Phase 28).
        if (!applied.Contains(19))
        {
            ApplyTolerantStatements(conn, 19, Migrations_V19.Statements);
        }

        // V20 — app_settings + notifications (Phase 29).
        if (!applied.Contains(20))
        {
            ApplyTolerantStatements(conn, 20, Migrations_V20.Statements);
        }

        // V21 — external tester results (Phase 31 follow-up).
        if (!applied.Contains(21))
        {
            ApplyTolerantStatements(conn, 21, Migrations_V21.Statements);
        }

        // V22 — Advertisement section (Phase 34): domain_lists,
        // competitor_records, action_events, runs.ip_used,
        // overview_widgets.
        if (!applied.Contains(22))
        {
            ApplyTolerantStatements(conn, 22, Migrations_V22.Statements);
        }

        // V23 — drop+recreate competitor_records + action_events to
        // fix dev-machine DBs that landed an early V22 with a
        // different column shape. See Migrations_V23.cs for the full
        // rationale. Cheap on fresh installs (drops are no-ops).
        if (!applied.Contains(23))
        {
            ApplyTolerantStatements(conn, 23, Migrations_V23.Statements);
        }

        // V24 — adds tests_json column to selfcheck_results so we can
        // store per-probe outcomes (~25 named tests covering navigator,
        // screen, timezone, webgl, canvas, audio, plugins, automation).
        // Tolerant: ALTER TABLE … ADD COLUMN throws "duplicate column"
        // on re-run, which the helper swallows.
        if (!applied.Contains(24))
        {
            ApplyTolerantStatements(conn, 24, Migrations_V24.Statements);
        }

        // V25 — backfill app_settings.updated_at column. V1 created the
        // table with only (key, value); V20 re-declared it with the
        // modern shape including updated_at via CREATE TABLE IF NOT
        // EXISTS — which is a no-op when the table already exists.
        // So pre-V20 databases never got the column, every SettingsService
        // SetStringAsync UPSERT failed silently with "no column named
        // updated_at", and every Settings checkbox/text change failed
        // to persist across restarts. Tolerant: ALTER TABLE on a fresh
        // DB created via V20 will throw "duplicate column" which the
        // helper swallows.
        if (!applied.Contains(25))
        {
            ApplyTolerantStatements(conn, 25, Migrations_V25.Statements);
        }
    }

    private void ApplyMigration(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        int version, string sql, bool tolerateDuplicateColumn)
    {
        _log.LogInformation("Applying migration v{Version}", version);

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            try { cmd.ExecuteNonQuery(); }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
                when (tolerateDuplicateColumn && IsDuplicateColumn(ex))
            {
                _log.LogInformation("Migration v{V} statement skipped (already applied)", version);
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO __schema_version (version, applied_at) VALUES ($v, $t);";
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>
    /// Apply a list of statements with per-statement
    /// duplicate-column tolerance. Used by V11 + V13 — both have
    /// ALTER TABLE statements that aren't natively idempotent in
    /// SQLite. Stamping __schema_version happens once all statements
    /// have either succeeded or harmlessly skipped.
    /// </summary>
    private void ApplyTolerantStatements(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        int version, IReadOnlyList<string> statements)
    {
        _log.LogInformation("Applying migration v{V} (tolerant, {Count} stmts)",
            version, statements.Count);

        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            try { cmd.ExecuteNonQuery(); }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
                when (IsDuplicateColumn(ex))
            {
                _log.LogInformation("V{V} statement skipped (already applied)", version);
            }
        }

        using var stamp = conn.CreateCommand();
        stamp.CommandText = "INSERT INTO __schema_version (version, applied_at) VALUES ($v, $t);";
        stamp.Parameters.AddWithValue("$v", version);
        stamp.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        stamp.ExecuteNonQuery();
    }

    private static bool IsDuplicateColumn(Microsoft.Data.Sqlite.SqliteException ex)
        => ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase);

    private static void EnsureVersionTable(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS __schema_version (
                version    INTEGER PRIMARY KEY,
                applied_at TEXT    NOT NULL
            );
        """;
        cmd.ExecuteNonQuery();
    }

    private static HashSet<int> LoadAppliedVersions(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT version FROM __schema_version;";
        using var rdr = cmd.ExecuteReader();
        var seen = new HashSet<int>();
        while (rdr.Read()) seen.Add(rdr.GetInt32(0));
        return seen;
    }
}

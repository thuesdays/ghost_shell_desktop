// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V13 — Scripts feature (Phase 12).
///
/// Idempotent: applied via the tolerant-statement runner path so a
/// partial previous run can re-execute without crashing on
/// "duplicate column 'assigned_script_id'".
/// </summary>
internal static class Migrations_V13
{
    /// <summary>
    /// Statements applied in order with per-statement
    /// duplicate-column tolerance. Same dispatch path as V11.
    /// </summary>
    internal static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS scripts (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT    NOT NULL UNIQUE,
            description TEXT,
            steps_json  TEXT    NOT NULL DEFAULT '[]',
            enabled     INTEGER NOT NULL DEFAULT 1,
            etag        TEXT    NOT NULL DEFAULT '',
            created_at  TEXT    NOT NULL,
            updated_at  TEXT    NOT NULL
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_scripts_enabled ON scripts(enabled);",
        """
        CREATE TABLE IF NOT EXISTS script_runs (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            script_id       INTEGER NOT NULL,
            profile_name    TEXT    NOT NULL,
            started_at      TEXT    NOT NULL,
            finished_at     TEXT,
            status          TEXT    NOT NULL DEFAULT 'running',
            steps_executed  INTEGER NOT NULL DEFAULT 0,
            steps_failed    INTEGER NOT NULL DEFAULT 0,
            ads_clicked     INTEGER NOT NULL DEFAULT 0,
            duration_sec    REAL,
            last_error      TEXT,
            log_json        TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_script_runs_script  ON script_runs(script_id);",
        "CREATE INDEX IF NOT EXISTS idx_script_runs_started ON script_runs(started_at DESC);",
        "CREATE INDEX IF NOT EXISTS idx_script_runs_status  ON script_runs(status);",
        // Per-profile script binding. NOT idempotent at SQLite level,
        // hence the tolerant runner path.
        "ALTER TABLE profiles ADD COLUMN assigned_script_id INTEGER;",
    };
}

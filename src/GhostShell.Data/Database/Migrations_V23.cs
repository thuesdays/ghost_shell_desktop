// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V23 — re-create <c>competitor_records</c> + <c>action_events</c>
/// with the correct V22 schema.
///
/// Why this exists
/// ---------------
/// During Phase 34 development the V22 migration shipped TWICE — once
/// with an early column shape (no <c>captured_at</c>) and a second time
/// with the final shape used by <see cref="GhostShell.Data.Services.CompetitorService"/>
/// and <see cref="GhostShell.Data.Services.AdDensityService"/>. Users
/// who ran the app between those two checkins now have the OLD table
/// schema, and queries against <c>captured_at</c> raise
/// <c>SqliteException: no such column: captured_at</c>.
///
/// SQLite has no <c>ALTER TABLE … ADD COLUMN IF NOT EXISTS</c>, and
/// the columns the new schema needs may differ in name (not just
/// missing). Cleanest fix: drop both tables outright and re-create
/// them. Data loss is acceptable here — both tables have only been
/// populated by post-Phase-34 dev runs and by the new
/// ScriptRunner hooks (RecordBatchAsync / RecordActionAsync), so
/// nothing the user has invested time curating lives there yet.
///
/// Idempotent: if the tables already match the V22 schema, the DROP
/// is a no-op and the CREATE re-emits identical schema. Re-running
/// V23 is safe.
/// </summary>
internal static class Migrations_V23
{
    internal static readonly string[] Statements =
    {
        // Drop old (possibly malformed) tables. DROP TABLE IF EXISTS
        // never errors on a missing table — safe for fresh DBs too.
        "DROP TABLE IF EXISTS competitor_records;",
        "DROP TABLE IF EXISTS action_events;",

        // Re-create competitor_records with the canonical schema.
        // Mirrors V22 verbatim so the two stay in lockstep — if a
        // future column gets added, update BOTH places (or accept the
        // re-create cost of a V24).
        """
        CREATE TABLE IF NOT EXISTS competitor_records (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id            INTEGER,
            profile_name      TEXT,
            captured_at       TEXT    NOT NULL,
            query             TEXT    NOT NULL,
            domain            TEXT    NOT NULL,
            ad_title          TEXT,
            display_url       TEXT,
            clean_url         TEXT,
            click_url         TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_competitors_domain ON competitor_records(domain);",
        "CREATE INDEX IF NOT EXISTS idx_competitors_query  ON competitor_records(query);",
        "CREATE INDEX IF NOT EXISTS idx_competitors_when   ON competitor_records(captured_at DESC);",
        "CREATE INDEX IF NOT EXISTS idx_competitors_run    ON competitor_records(run_id);",

        """
        CREATE TABLE IF NOT EXISTS action_events (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id        INTEGER,
            profile_name  TEXT    NOT NULL,
            captured_at   TEXT    NOT NULL,
            query         TEXT,
            ad_domain     TEXT,
            ad_class      TEXT,
            action_type   TEXT    NOT NULL,
            outcome       TEXT    NOT NULL,
            skip_reason   TEXT,
            duration_sec  REAL,
            error         TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_aev_run        ON action_events(run_id);",
        "CREATE INDEX IF NOT EXISTS idx_aev_profile_ts ON action_events(profile_name, captured_at DESC);",
        "CREATE INDEX IF NOT EXISTS idx_aev_domain_ts  ON action_events(ad_domain, captured_at DESC);",
    };
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V6 — Sessions &amp; Cookies feature port (Phase 4.2).
///
/// Two new tables that mirror the legacy
/// <c>cookie_snapshots</c> + <c>cookie_packs</c> tables from
/// <c>db/database.py</c>, with a few simplifications appropriate to
/// a single-user desktop app:
///
///   • <c>cookie_snapshots</c> stores per-run cookie + localStorage
///     captures. Auto-saved on clean shutdown, restoreable from the
///     UI. JSON columns instead of gzipped BLOBs — snapshots are
///     small (10s of KB) and human-readable JSON makes debugging
///     trivial. The legacy gzip is kept for the (much larger)
///     pack format below.
///
///   • <c>cookie_packs</c> stores portable cookie bundles — labelled,
///     domain-tagged, often hundreds of cookies + multiple
///     localStorage origins. Payload is gzipped JSON to keep the DB
///     compact when the user imports a 2 MB pack from disk. Slug
///     is the natural key for re-imports (UNIQUE).
///
/// Both tables are append-mostly with manual deletion via the UI.
/// No foreign keys to <c>profiles</c> on snapshots — when a profile
/// is deleted, its snapshots stay around as a recovery resource
/// (legacy behaviour).
/// </summary>
internal static class Migrations_V6
{
    internal const string Sql = """
        -- ─── cookie_snapshots ───────────────────────────────────
        CREATE TABLE IF NOT EXISTS cookie_snapshots (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_name  TEXT    NOT NULL,
            created_at    TEXT    NOT NULL,
            run_id        INTEGER,
            trigger       TEXT,
            cookies_json  TEXT    NOT NULL,
            storage_json  TEXT    NOT NULL,
            cookie_count  INTEGER NOT NULL DEFAULT 0,
            domain_count  INTEGER NOT NULL DEFAULT 0,
            bytes         INTEGER NOT NULL DEFAULT 0,
            reason        TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_snap_profile ON cookie_snapshots(profile_name);
        CREATE INDEX IF NOT EXISTS idx_snap_created ON cookie_snapshots(created_at DESC);

        -- ─── cookie_packs ──────────────────────────────────────
        CREATE TABLE IF NOT EXISTS cookie_packs (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            slug            TEXT    NOT NULL UNIQUE,
            label           TEXT    NOT NULL,
            domains         TEXT    NOT NULL,
            age_days        INTEGER NOT NULL DEFAULT 0,
            captcha_rate    REAL    NOT NULL DEFAULT 0.0,
            payload_gz      BLOB    NOT NULL,
            cookies_count   INTEGER NOT NULL DEFAULT 0,
            storage_count   INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_packs_label ON cookie_packs(label);
    """;
}

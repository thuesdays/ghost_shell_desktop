// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V1 schema: profiles, runs, proxies, app_settings.
///
/// Mirrors the *subset* of the legacy schema we need today.
/// Note: while still in early dev (Phase 2), adding columns here is
/// fine — there are no production databases to migrate. Once we
/// ship, schema changes go in V2/V3 as additive migrations.
/// </summary>
internal static class Migrations_V1
{
    internal const string Sql = """
        -- ─── profiles ───
        CREATE TABLE IF NOT EXISTS profiles (
            name         TEXT PRIMARY KEY,
            group_name   TEXT,
            template_id  TEXT,
            proxy_slug   TEXT,
            is_ready     INTEGER NOT NULL DEFAULT 0,
            last_run_at  TEXT,
            run_count    INTEGER NOT NULL DEFAULT 0,
            note         TEXT,
            created_at   TEXT NOT NULL,
            updated_at   TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_profiles_group ON profiles(group_name);

        -- ─── runs ───
        CREATE TABLE IF NOT EXISTS runs (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_name  TEXT    NOT NULL,
            started_at    TEXT    NOT NULL,
            finished_at   TEXT,
            exit_code     INTEGER,
            total_queries INTEGER NOT NULL DEFAULT 0,
            total_ads     INTEGER NOT NULL DEFAULT 0,
            captchas      INTEGER NOT NULL DEFAULT 0,
            last_error    TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_runs_profile  ON runs(profile_name);
        CREATE INDEX IF NOT EXISTS idx_runs_started  ON runs(started_at DESC);
        CREATE INDEX IF NOT EXISTS idx_runs_finished ON runs(finished_at);

        -- ─── proxies (URL-shaped, like the legacy web project) ───
        CREATE TABLE IF NOT EXISTS proxies (
            slug              TEXT PRIMARY KEY,
            name              TEXT,
            url               TEXT NOT NULL,
            is_rotating       INTEGER NOT NULL DEFAULT 0,
            rotation_api_url  TEXT,
            rotation_provider TEXT,
            rotation_api_key  TEXT,
            is_default        INTEGER NOT NULL DEFAULT 0,
            notes             TEXT,
            last_ip           TEXT,
            country           TEXT,
            city              TEXT,
            health            TEXT NOT NULL DEFAULT 'unknown',
            last_checked_at   TEXT,
            created_at        TEXT NOT NULL,
            updated_at        TEXT NOT NULL
        );
        -- Only one row may carry is_default=1. Enforced at INSERT time
        -- in the service layer; this index is for fast "find default" lookups.
        CREATE INDEX IF NOT EXISTS idx_proxies_default ON proxies(is_default);

        -- ─── app settings (key/value JSON store) ───
        CREATE TABLE IF NOT EXISTS app_settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
    """;
}

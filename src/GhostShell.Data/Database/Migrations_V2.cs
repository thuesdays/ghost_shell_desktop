// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V2 — restructure `proxies` table to match the legacy web project's
/// shape (single Url field + rotation/default/notes, instead of
/// host/port/kind/username/password as separate columns).
///
/// We're still pre-release with no real users, so it's safe to drop
/// and recreate. Once we ship, schema changes go via ALTER TABLE in
/// later migrations rather than wiping rows.
/// </summary>
internal static class Migrations_V2
{
    internal const string Sql = """
        DROP TABLE IF EXISTS proxies;

        CREATE TABLE proxies (
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
        CREATE INDEX IF NOT EXISTS idx_proxies_default ON proxies(is_default);
    """;
}

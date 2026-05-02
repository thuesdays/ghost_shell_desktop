// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V18 — Browser Extensions (Phase 27).
///
/// Lets the user manage Chrome extensions across profiles. Mirrors the
/// "load every extension by default, opt out per profile" model the
/// user asked for.
///
///   • <c>extensions</c> stores the GLOBAL library — one row per
///     extension installed via the Extensions page (zip / crx / store /
///     unpacked folder). The unpacked extension files live on disk under
///     <c>%LocalAppData%\GhostShell\extensions\&lt;id&gt;\</c>; we keep
///     only metadata + a pointer in this row.
///   • <c>profile_extensions</c> is a per-profile override row. A
///     profile inherits the GLOBAL <c>enabled</c> flag from
///     <c>extensions</c> by default; insert a row here to flip the
///     state on/off just for that profile.
///   • <c>extension_store_cache</c> remembers the icon / description
///     for store-installed extensions so the UI doesn't re-fetch them
///     every time the page renders.
///
/// FK-less by design (matches the rest of the schema). Service-layer
/// cleanup paths cascade on profile delete + extension delete.
/// </summary>
internal static class Migrations_V18
{
    internal static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS extensions (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            ext_id          TEXT    NOT NULL,                    -- 32-char Chrome extension ID
            name            TEXT    NOT NULL,
            version         TEXT    NOT NULL DEFAULT '0.0.0',
            description     TEXT,
            author          TEXT,
            homepage        TEXT,
            source          TEXT    NOT NULL DEFAULT 'unknown',  -- store | zip | crx | folder
            install_url     TEXT,                                -- original URL or file path used to install
            local_path      TEXT    NOT NULL,                    -- unpacked extension dir on disk
            manifest_json   TEXT,                                -- raw manifest.json contents
            icon_path       TEXT,                                -- path to icon file inside local_path
            permissions_json TEXT,                               -- JSON array of permission strings
            host_permissions_json TEXT,                          -- JSON array of host_permissions strings
            enabled         INTEGER NOT NULL DEFAULT 1,          -- global default for new profiles
            pinned          INTEGER NOT NULL DEFAULT 0,          -- show in toolbar by default
            created_at      TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
            updated_at      TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_extensions_ext_id ON extensions(ext_id);",
        "CREATE INDEX IF NOT EXISTS idx_extensions_source ON extensions(source);",
        "CREATE INDEX IF NOT EXISTS idx_extensions_enabled ON extensions(enabled);",

        """
        CREATE TABLE IF NOT EXISTS profile_extensions (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_name  TEXT    NOT NULL,
            extension_id  INTEGER NOT NULL,                       -- FK -> extensions.id
            enabled       INTEGER NOT NULL DEFAULT 1,             -- per-profile override of extensions.enabled
            updated_at    TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_profile_extensions ON profile_extensions(profile_name, extension_id);",
        "CREATE INDEX IF NOT EXISTS idx_profile_extensions_profile ON profile_extensions(profile_name);",
        "CREATE INDEX IF NOT EXISTS idx_profile_extensions_ext     ON profile_extensions(extension_id);",

        """
        CREATE TABLE IF NOT EXISTS extension_store_cache (
            ext_id       TEXT PRIMARY KEY,
            name         TEXT,
            description  TEXT,
            author       TEXT,
            icon_url     TEXT,
            rating       REAL,
            users        INTEGER,
            last_seen_at TEXT NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,
    };
}

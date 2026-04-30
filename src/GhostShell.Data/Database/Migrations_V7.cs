// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V7 — Profile Groups (Phase 4.5).
///
/// Groups let the user batch-launch many profiles with a single click.
/// Mirrors the legacy <c>groups</c> + <c>group_members</c> tables from
/// <c>db/database.py</c>:
///
///   • <c>profile_groups</c>   — one row per group. Free-form name +
///                               optional description + optional
///                               max_parallel cap that overrides the
///                               global runner concurrency limit.
///                               (Cap is null = inherit global.)
///
///   • <c>profile_group_members</c> — many-to-many join. Same profile
///                               can belong to many groups (e.g.
///                               "All Facebook" + "US East").
///                               PRIMARY KEY (group_id, profile_name)
///                               so duplicates can't sneak in. Foreign
///                               keys CASCADE on group delete; profile
///                               deletes are handled in the service
///                               (named-key, not row-key, so no FK).
///
/// Both tables stay tiny (low double-digit rows in real use) so
/// indexes beyond the implicit ones aren't necessary.
/// </summary>
internal static class Migrations_V7
{
    internal const string Sql = """
        -- ─── profile_groups ────────────────────────────────────
        CREATE TABLE IF NOT EXISTS profile_groups (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            name          TEXT    NOT NULL UNIQUE,
            description   TEXT,
            max_parallel  INTEGER,
            created_at    TEXT    NOT NULL,
            updated_at    TEXT    NOT NULL
        );

        -- ─── profile_group_members ─────────────────────────────
        CREATE TABLE IF NOT EXISTS profile_group_members (
            group_id      INTEGER NOT NULL,
            profile_name  TEXT    NOT NULL,
            PRIMARY KEY (group_id, profile_name),
            FOREIGN KEY (group_id) REFERENCES profile_groups(id) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS idx_group_members_profile
            ON profile_group_members(profile_name);
    """;
}

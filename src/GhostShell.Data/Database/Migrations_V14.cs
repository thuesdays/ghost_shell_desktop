// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V14 — Scripts <c>is_default</c> + <c>script_run_id</c> on runs.
///
/// One: scripts can be marked default — <see cref="ProfileService"/>
/// auto-assigns the default script to fresh profiles.
///
/// Two: <c>runs</c> learns about scripts so the Runs page can show
/// "this run executed script X". Nullable because legacy runs
/// (warmup-only / monitor-only) have no script.
/// </summary>
internal static class Migrations_V14
{
    internal static readonly string[] Statements =
    {
        "ALTER TABLE scripts ADD COLUMN is_default INTEGER NOT NULL DEFAULT 0;",
        "CREATE INDEX IF NOT EXISTS idx_scripts_default ON scripts(is_default);",
        "ALTER TABLE runs ADD COLUMN script_run_id INTEGER;",
    };
}

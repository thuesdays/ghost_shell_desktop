// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V4 — extends the `profiles` table with two new fields the
/// profile editor now collects: preferred language tag and the
/// "enrich on first run" toggle. Plain ALTER TABLE — non-breaking
/// for existing rows (defaults below cover legacy data).
/// </summary>
internal static class Migrations_V4
{
    internal const string Sql = """
        ALTER TABLE profiles ADD COLUMN language             TEXT;
        ALTER TABLE profiles ADD COLUMN enrich_on_first_run  INTEGER NOT NULL DEFAULT 1;
    """;
}

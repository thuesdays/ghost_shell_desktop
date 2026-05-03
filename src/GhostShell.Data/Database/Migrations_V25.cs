// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V25 — Backfill <c>app_settings.updated_at</c> column.
///
/// Bug discovered Phase 62: <c>Migrations_V1</c> creates app_settings
/// with only (key, value) — no <c>updated_at</c>. <c>Migrations_V20</c>
/// (Phase 29) re-declared the table including the column, but the
/// statement uses <c>CREATE TABLE IF NOT EXISTS</c>, which is a no-op
/// when the table already exists. So databases bootstrapped on V1+
/// never gained the column, and every <see cref="GhostShell.Data.Services.SettingsService"/>
/// SetStringAsync call fails with
/// <c>SQLite Error 1: 'table app_settings has no column named updated_at'</c> —
/// silently in production (caught + logged), making Settings page
/// changes never persist across restarts.
///
/// This migration adds the column via ALTER TABLE. Wrapped in the
/// MigrationRunner's tolerant-statement path so a fresh DB (where V20
/// already created the column inline) doesn't error on duplicate
/// column-add.
/// </summary>
internal static class Migrations_V25
{
    internal static readonly string[] Statements =
    {
        // ALTER TABLE … ADD COLUMN — SQLite supports this since 3.2.
        // The DEFAULT CURRENT_TIMESTAMP backfills existing rows with
        // the migration-time stamp; subsequent UPSERTs from
        // SettingsService overwrite per-key as users change values.
        // ApplyTolerantStatements swallows "duplicate column name"
        // errors so a fresh DB created with V20's modern shape
        // doesn't choke here.
        "ALTER TABLE app_settings ADD COLUMN updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP;",
    };
}

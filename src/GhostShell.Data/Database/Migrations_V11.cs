// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V11 — Fingerprint regen / noise salts + audit log.
///
/// Two new columns on <c>profiles</c> drive the deterministic-but-
/// regenerable fingerprint pipeline:
///
///   • <c>fp_regen_salt</c>  — bumped on full regenerate; mixes into
///                             the per-profile SHA-256 seed for the
///                             entire payload.
///   • <c>fp_noise_salt</c>  — bumped on reshuffle; mixes into the
///                             noise-fields seed only.
///
/// Plus a <c>fingerprint_audits</c> log table.
///
/// Idempotency: SQLite has no <c>ALTER TABLE ADD COLUMN IF NOT EXISTS</c>,
/// so we apply each statement independently via the
/// <c>MigrationRunner.ApplyV11Tolerantly</c> path. Duplicate-column
/// errors get swallowed — that means a previous partial run can
/// safely re-execute without crashing on startup.
/// </summary>
internal static class Migrations_V11
{
    /// <summary>
    /// Statements applied in order with per-statement
    /// duplicate-column tolerance.
    /// </summary>
    internal static readonly string[] Statements =
    {
        "ALTER TABLE profiles ADD COLUMN fp_regen_salt TEXT;",
        "ALTER TABLE profiles ADD COLUMN fp_noise_salt TEXT;",
        """
        CREATE TABLE IF NOT EXISTS fingerprint_audits (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_name  TEXT    NOT NULL,
            generated_at  TEXT    NOT NULL,
            score         INTEGER NOT NULL,
            template_id   TEXT    NOT NULL,
            note          TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_fp_audits_profile  ON fingerprint_audits(profile_name);",
        "CREATE INDEX IF NOT EXISTS idx_fp_audits_generated ON fingerprint_audits(generated_at DESC);",
    };
}

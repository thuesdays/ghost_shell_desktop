// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V21 — External-tester probe results (Phase 31 follow-up).
///
/// One row per (profile, tester). The Fingerprint page upserts a row
/// after each "Probe in profile" click; the next time the user opens
/// the page the cards restore their last verdict instead of starting
/// blank. <c>details_json</c> is the serialised TesterResult.Details
/// list so the detail dialog can replay the per-key rows verbatim.
/// </summary>
internal static class Migrations_V21
{
    internal static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS external_tester_results (
            profile_name  TEXT NOT NULL,
            tester_name   TEXT NOT NULL,
            summary       TEXT,
            verdict       TEXT,
            details_json  TEXT,
            captured_at   TEXT NOT NULL DEFAULT (CURRENT_TIMESTAMP),
            PRIMARY KEY (profile_name, tester_name)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_etr_profile ON external_tester_results(profile_name);",
    };
}

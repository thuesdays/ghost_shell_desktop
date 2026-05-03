// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V24 — Add per-probe test results column to selfcheck_results.
///
/// Extends the runtime self-check table with detailed per-probe results,
/// moving from a single 0-100 score to a comprehensive set of named test
/// outcomes (navigator.userAgent, screen.width, timezone.id, etc.).
/// This mirrors the legacy web's ~40-probe model and enables per-test
/// card rendering in the UI with expected vs actual diff views.
///
/// The tests_json column is JSON-serialised array of SelfCheckTestResult
/// objects. Defaults to "[]" for backwards compatibility with older runs
/// (pre-V24 rows won't have per-probe data, only the legacy top-level
/// fields like exit_ip, webrtc_leaked, score).
/// </summary>
internal static class Migrations_V24
{
    internal static readonly string[] Statements =
    {
        // Add the per-probe test results column. The column contains
        // a JSON array serialised from List<SelfCheckTestResult>. This
        // allows the self-check service to populate granular test data
        // while keeping the legacy score/exit_ip/webrtc fields intact
        // for backwards compat.
        "ALTER TABLE selfcheck_results ADD COLUMN tests_json TEXT NOT NULL DEFAULT '[]';",
    };
}

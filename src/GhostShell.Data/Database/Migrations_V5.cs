// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V5 — extends `runs` with the two columns the watchdog port (Phase
/// 4.1) needs to mirror the legacy Python runtime's behaviour:
///
///   • <c>heartbeat_at</c> — refreshed every 30s while a session is
///     alive. Stale-vs-fresh threshold lives in code (180s) so we
///     can tune it without another migration. Lets the dashboard
///     show "wedged" runs (PID alive but heartbeat stale → hung).
///
///   • <c>stop_reason</c> — short tag describing how the run ended:
///     "clean", "external_close", "crash", "rotation_failed",
///     "needs_attention", "user_cancelled". Distinct from
///     <c>last_error</c> (free-form message) and <c>exit_code</c>
///     (numeric). The Runs page uses it for colour-coding so the
///     user can tell at a glance whether their proxy died or they
///     closed the window themselves.
///
/// Both columns are nullable / default — old rows survive untouched.
/// </summary>
internal static class Migrations_V5
{
    internal const string Sql = """
        ALTER TABLE runs ADD COLUMN heartbeat_at TEXT;
        ALTER TABLE runs ADD COLUMN stop_reason  TEXT;
        CREATE INDEX IF NOT EXISTS idx_runs_heartbeat ON runs(heartbeat_at);
    """;
}

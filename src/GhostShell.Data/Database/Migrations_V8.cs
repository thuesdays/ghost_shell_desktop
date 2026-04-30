// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V8 — Scheduler (Phase 5).
///
/// One row per scheduled task. The desktop scheduler is a slimmed
/// port of the legacy web's <c>scheduled_tasks</c> + <c>scheduler</c>
/// config-key bag — we keep cron + simple interval triggers, skip
/// the legacy "density" mode (target-runs-per-day with jitter, often
/// confused users), and fold per-schedule active-hours / active-days
/// into the row so each schedule is fully self-describing.
///
/// Trigger kinds:
///   • <c>cron</c>     — 5-field cron expression in <c>cron_expr</c>
///   • <c>interval</c> — fixed interval in <c>interval_sec</c>
///
/// Target kinds:
///   • <c>profile</c>  — fires a single profile run by name
///   • <c>group</c>    — fires every member of the group (uses the
///                       group's max-parallel cap as the limit)
///
/// Active-window guards:
///   • <c>active_days</c>   — CSV of ISO weekday numbers (1=Mon … 7=Sun);
///                            empty string = every day
///   • <c>active_from_hour</c> / <c>active_to_hour</c>
///                          — 0-23 inclusive; null = any hour
///
/// State:
///   • <c>last_fired_at</c> — last time the scheduler kicked a run
///   • <c>next_fire_at</c>  — when the loop will fire it next (precomputed
///                            so the UI can render a countdown without
///                            reparsing the cron expression)
///   • <c>fire_count</c>    — total successful fires
///   • <c>fail_count</c>    — consecutive failed launches; cleared on success
/// </summary>
internal static class Migrations_V8
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS schedules (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            name            TEXT    NOT NULL,
            target_kind     TEXT    NOT NULL,
            target_name     TEXT    NOT NULL,
            trigger_kind    TEXT    NOT NULL,
            cron_expr       TEXT,
            interval_sec    INTEGER,
            active_days     TEXT    NOT NULL DEFAULT '',
            active_from_hour INTEGER,
            active_to_hour   INTEGER,
            enabled         INTEGER NOT NULL DEFAULT 1,
            last_fired_at   TEXT,
            next_fire_at    TEXT,
            fire_count      INTEGER NOT NULL DEFAULT 0,
            fail_count      INTEGER NOT NULL DEFAULT 0,
            created_at      TEXT    NOT NULL,
            updated_at      TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_schedules_enabled  ON schedules(enabled);
        CREATE INDEX IF NOT EXISTS idx_schedules_next_fire ON schedules(next_fire_at);
    """;
}

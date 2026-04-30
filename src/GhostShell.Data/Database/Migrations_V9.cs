// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V9 — Warmup robot (Phase 6).
///
/// One row per warmup invocation, written by <c>WarmupService</c>.
/// Mirrors the legacy Python <c>warmup_runs</c> table 1:1 so the
/// Sessions / Cookies page (the redesigned hub) renders the same
/// "Warmup history" table content the web ships.
///
/// Lifecycle:
///   1. <see cref="Sql"/> creates the table empty.
///   2. <c>WarmupService.StartAsync</c> INSERTs a row with
///      <c>status = 'running'</c> and <c>finished_at = NULL</c>;
///      the row id becomes the in-memory engine handle.
///   3. The engine ticks each site, appending to <c>sites_log</c>
///      (in-memory only — SQLite write happens once at finish to
///      avoid contention on the queue lock).
///   4. <c>FinishAsync</c> UPDATEs status / counts / duration / log.
///
/// Status values: <c>running | ok | partial | failed</c>.
/// Trigger values: <c>manual | scheduled | auto_quality | auto_before_run</c>
/// (extensible — we don't enum-constrain the DB so future code can
/// introduce new triggers without a migration).
///
/// <c>sites_log</c> is a JSON array of per-site result records.
/// Stored as TEXT (not BLOB) because the rows are small (≤2KB) and
/// JSON-as-text plays nicely with sqlite CLI / DB Browser inspection.
/// </summary>
internal static class Migrations_V9
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS warmup_runs (
            id              INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_name    TEXT    NOT NULL,
            started_at      TEXT    NOT NULL,
            finished_at     TEXT,
            preset          TEXT,
            sites_planned   INTEGER NOT NULL DEFAULT 0,
            sites_visited   INTEGER NOT NULL DEFAULT 0,
            sites_succeeded INTEGER NOT NULL DEFAULT 0,
            duration_sec    REAL,
            status          TEXT    NOT NULL DEFAULT 'running',
            trigger         TEXT    NOT NULL DEFAULT 'manual',
            notes           TEXT,
            sites_log       TEXT    NOT NULL DEFAULT '[]'
        );
        CREATE INDEX IF NOT EXISTS idx_warmup_profile ON warmup_runs(profile_name);
        CREATE INDEX IF NOT EXISTS idx_warmup_started ON warmup_runs(started_at DESC);
        CREATE INDEX IF NOT EXISTS idx_warmup_status  ON warmup_runs(status);
    """;
}

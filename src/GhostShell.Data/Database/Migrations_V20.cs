// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V20 — Settings + Notifications (Phase 29).
///
///   • <c>app_settings</c> — flat key/value bag for everything the user
///     can tweak from the Settings page (UA spoof range, Chromium
///     binary path, SERP engagement toggles, blocking rules, auto-
///     enrich knobs, etc.). Mirrors the legacy web's "config" table —
///     dotted keys keep the schema future-proof.
///   • <c>notifications</c> — persisted bell-drawer items. The web
///     project re-aggregated these on each fetch from other tables;
///     the desktop app keeps a small write-through table so the bell
///     can show recent items even if the underlying source was
///     cleared (e.g. a captcha spike still surfaces after the run
///     row is purged). Dismissed-at lets the UI hide individual
///     entries without deleting them — a "show dismissed" toggle
///     can resurrect them.
/// </summary>
internal static class Migrations_V20
{
    internal static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS app_settings (
            key        TEXT PRIMARY KEY,
            value      TEXT,
            updated_at TEXT NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,

        """
        CREATE TABLE IF NOT EXISTS notifications (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            severity    TEXT    NOT NULL DEFAULT 'info',     -- info | warning | critical | success
            title       TEXT    NOT NULL,
            body        TEXT,
            action      TEXT,                                -- open_profile | open_proxy | open_runs | open_scheduler | url
            action_arg  TEXT,
            source      TEXT    NOT NULL DEFAULT 'manual',   -- short tag for grouping (e.g. 'run_failed', 'update_check')
            created_at  TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
            dismissed_at TEXT
        );
        """,
        // Phase 29 audit fix — partial index. The bell drawer's main
        // query is `WHERE dismissed_at IS NULL`. A full index over the
        // column stores every dismissed row's timestamp too, which is
        // dead weight (we never query against those values). The
        // partial form indexes ONLY the active rows we actually scan.
        "CREATE INDEX IF NOT EXISTS idx_notif_active ON notifications(created_at DESC) WHERE dismissed_at IS NULL;",
        "CREATE INDEX IF NOT EXISTS idx_notif_created   ON notifications(created_at DESC);",
        "CREATE INDEX IF NOT EXISTS idx_notif_severity  ON notifications(severity);",
    };
}

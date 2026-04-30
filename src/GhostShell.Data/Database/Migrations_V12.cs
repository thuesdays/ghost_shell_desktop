// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V12 — Self-check probe results.
///
/// Persists per-launch network-layer probe outcomes: exit IP, geo
/// country / city, WebRTC leak detection, timezone actual vs
/// expected, captured navigator.userAgent. The Fingerprint page's
/// "Runtime self-check" section reads from this table.
///
/// One row per probe execution. The probe runs on every Nth profile
/// launch (Nth gate is in-memory state in SelfCheckService).
/// </summary>
internal static class Migrations_V12
{
    internal const string Sql = """
        CREATE TABLE IF NOT EXISTS selfcheck_results (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            profile_name      TEXT    NOT NULL,
            run_id            INTEGER,
            ran_at            TEXT    NOT NULL,
            exit_ip           TEXT,
            geo_country       TEXT,
            geo_city          TEXT,
            webrtc_leaked     INTEGER NOT NULL DEFAULT 0,
            webrtc_local_ip   TEXT,
            timezone_actual   TEXT,
            timezone_expected TEXT,
            ua_actual         TEXT,
            score             INTEGER NOT NULL DEFAULT 0,
            note              TEXT,
            raw_json          TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_selfcheck_profile
            ON selfcheck_results(profile_name);
        CREATE INDEX IF NOT EXISTS idx_selfcheck_ran
            ON selfcheck_results(ran_at DESC);
    """;
}

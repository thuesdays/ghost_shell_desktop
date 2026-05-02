// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V19 — Traffic accounting (Phase 28).
///
/// Mirrors the legacy web project's <c>traffic_stats</c> table. One row
/// per (profile, domain, hour bucket) — the hour bucket is the local
/// time formatted as <c>YYYY-MM-DD HH</c> so per-day rollups are a
/// trivial substring on the column. Bytes / req_count accumulate via
/// UPSERT on each flush.
///
/// We deliberately do NOT store URLs, headers, or response bodies —
/// only the hostname is captured. That keeps the table small (a few
/// hundred rows per profile per day at most), makes range queries
/// cheap, and avoids building an HTTP-archive-style request inspector
/// (different feature, much bigger surface).
///
/// Sources of byte data:
///   1. <c>ProxyAuthForwarder</c> — per-host TCP-level counters,
///      authoritative because they're measured below the browser's
///      cross-origin Timing-Allow-Origin masking.
///   2. <c>PerformanceObserver</c> JS injected into the page —
///      fallback for direct (non-proxied) connections; reliable for
///      request COUNTS but bytes are zeroed on cross-origin fetches
///      without TAO headers.
///
/// run_id captures the FIRST run that touched a bucket so the UI can
/// show "this profile/domain bucket originated in run #N", but bytes
/// keep accumulating across subsequent runs in the same hour.
/// </summary>
internal static class Migrations_V19
{
    internal static readonly string[] Statements =
    {
        """
        CREATE TABLE IF NOT EXISTS traffic_stats (
            profile_name  TEXT    NOT NULL,
            domain        TEXT    NOT NULL,
            hour_bucket   TEXT    NOT NULL,         -- 'YYYY-MM-DD HH' local time
            bytes         INTEGER NOT NULL DEFAULT 0,
            req_count     INTEGER NOT NULL DEFAULT 0,
            run_id        INTEGER,
            updated_at    TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP),
            PRIMARY KEY (profile_name, domain, hour_bucket)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_traffic_profile_time ON traffic_stats(profile_name, hour_bucket DESC);",
        "CREATE INDEX IF NOT EXISTS idx_traffic_bucket       ON traffic_stats(hour_bucket);",
        "CREATE INDEX IF NOT EXISTS idx_traffic_domain       ON traffic_stats(domain);",
    };
}

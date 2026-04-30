// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V3 — diagnostic columns on `proxies` (latency, country code,
/// asn/isp, ip_type) plus a new `proxy_health_events` log table that
/// feeds the timeline widget on the Proxy page.
///
/// Pre-release means we can use ALTER TABLE without worrying about
/// downstream consumers — the table is small.
/// </summary>
internal static class Migrations_V3
{
    internal const string Sql = """
        -- Add diagnostic columns to proxies. Wrapped in PRAGMA-guarded
        -- ALTERs is unnecessary in SQLite (you can re-add a column it
        -- already has and it errors), but our migration runner only
        -- runs each version once, so plain ALTERs are safe.
        ALTER TABLE proxies ADD COLUMN country_code TEXT;
        ALTER TABLE proxies ADD COLUMN asn          TEXT;
        ALTER TABLE proxies ADD COLUMN isp          TEXT;
        ALTER TABLE proxies ADD COLUMN ip_type      TEXT NOT NULL DEFAULT 'unknown';
        ALTER TABLE proxies ADD COLUMN latency_ms   INTEGER;

        -- Health timeline log. One row per meaningful event; keeps
        -- forever (UI filters by `since`). Indexed on slug+at for the
        -- per-proxy filter and on at for global timeline queries.
        CREATE TABLE IF NOT EXISTS proxy_health_events (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            proxy_slug  TEXT    NOT NULL,
            kind        TEXT    NOT NULL,
            at          TEXT    NOT NULL,
            detail      TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_phealth_slug_at ON proxy_health_events(proxy_slug, at DESC);
        CREATE INDEX IF NOT EXISTS idx_phealth_at      ON proxy_health_events(at DESC);
    """;
}

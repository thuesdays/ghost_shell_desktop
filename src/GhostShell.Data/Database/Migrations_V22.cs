// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V22 — Advertisement section (Phase 34).
///
/// Ports four interconnected legacy-web features:
///
///   1. <b>Domain lists</b> — three categorised lists used by the
///      script engine to decide which advertisers to click vs. skip:
///        • <c>my</c>     = "this is me" (skip — don't burn my own
///                           ad budget)
///        • <c>target</c> = "click these specifically" (high dwell,
///                           tracked separately)
///        • <c>block</c>  = "ignore entirely" (massive aggregators
///                           that dilute stats)
///      Stored as one row per (kind, domain) pair so we can index +
///      query by membership. Domains are kept lowercased + trimmed at
///      write time; the runner's predicate normalises again on read.
///
///   2. <b>Competitor records</b> — every advertiser observed in a
///      monitored query response. One row per (run, query, ad slot).
///      The Competitors page rolls these up into KPI tiles, a 7-day
///      line chart, and a leaderboard. The legacy schema is kept
///      verbatim so the analytics queries below (avg-mentions /
///      "new since" detection) port without changes.
///
///   3. <b>Action events</b> — per-step outcome log for the script
///      engine. Records WHY each ad was/wasn't clicked, so we can
///      compute CTR-proxy + skip-reason breakdowns and verify the
///      domain-list rules are firing correctly. <c>ad_class</c> is
///      one of {target, my_domain, competitor, unknown} and
///      <c>skip_reason</c> is the predicate that fired (used by the
///      Overview ad-density CTR tile).
///
///   4. <b>Overview widget layout</b> — per-user config for which
///      Overview tiles are visible + the order they render. We also
///      add an <c>ip_used</c> column to <c>runs</c> so the ad-density
///      widget can group by IP (matches the legacy "Top IPs · 7d"
///      table on the right side of the Ad density block).
///
/// FK-less by design (matches the rest of the schema). Service-layer
/// cleanup paths cascade on profile / run delete.
/// </summary>
internal static class Migrations_V22
{
    internal static readonly string[] Statements =
    {
        // ─── 1. Domain lists ─────────────────────────────────────────
        """
        CREATE TABLE IF NOT EXISTS domain_lists (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            kind        TEXT    NOT NULL,                          -- my | target | block
            domain      TEXT    NOT NULL,                          -- lowercased + trimmed
            note        TEXT,                                       -- optional human label
            created_at  TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,
        "CREATE UNIQUE INDEX IF NOT EXISTS ux_domain_lists ON domain_lists(kind, domain);",
        "CREATE INDEX IF NOT EXISTS idx_domain_lists_kind ON domain_lists(kind);",

        // ─── 2. Competitor records ───────────────────────────────────
        """
        CREATE TABLE IF NOT EXISTS competitor_records (
            id                INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id            INTEGER,                              -- FK -> runs.id (SET NULL on delete)
            profile_name      TEXT,
            captured_at       TEXT    NOT NULL,                     -- ISO-8601 UTC, when the ad was seen
            query             TEXT    NOT NULL,                     -- the search term that surfaced this ad
            domain            TEXT    NOT NULL,                     -- normalised competitor domain (lowercase)
            ad_title          TEXT,
            display_url       TEXT,
            clean_url         TEXT,
            click_url         TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_competitors_domain ON competitor_records(domain);",
        "CREATE INDEX IF NOT EXISTS idx_competitors_query  ON competitor_records(query);",
        "CREATE INDEX IF NOT EXISTS idx_competitors_when   ON competitor_records(captured_at DESC);",
        "CREATE INDEX IF NOT EXISTS idx_competitors_run    ON competitor_records(run_id);",

        // ─── 3. Action events (script-engine step log) ───────────────
        """
        CREATE TABLE IF NOT EXISTS action_events (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            run_id        INTEGER,                                  -- nullable so manual probes can log too
            profile_name  TEXT    NOT NULL,
            captured_at   TEXT    NOT NULL,
            query         TEXT,
            ad_domain     TEXT,
            ad_class      TEXT,                                      -- target | my_domain | competitor | unknown
            action_type   TEXT    NOT NULL,                          -- click_ad | dwell | scroll | etc.
            outcome       TEXT    NOT NULL,                          -- ran | skipped | error
            skip_reason   TEXT,                                      -- my_domain | target | not_target | not_my_domain | blocked | probability | disabled
            duration_sec  REAL,
            error         TEXT
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_aev_run        ON action_events(run_id);",
        "CREATE INDEX IF NOT EXISTS idx_aev_profile_ts ON action_events(profile_name, captured_at DESC);",
        "CREATE INDEX IF NOT EXISTS idx_aev_domain_ts  ON action_events(ad_domain, captured_at DESC);",

        // ─── 4. Runs.ip_used + overview widgets ─────────────────────
        // ip_used is added so the Overview ad-density tile can group
        // by IP. Older rows will have NULL — the dashboard SUM/AVG
        // queries already coalesce.
        "ALTER TABLE runs ADD COLUMN ip_used TEXT;",

        """
        CREATE TABLE IF NOT EXISTS overview_widgets (
            widget_id   TEXT    PRIMARY KEY,                         -- stable code id ("ad_density", "recent_runs", …)
            enabled     INTEGER NOT NULL DEFAULT 1,
            position    INTEGER NOT NULL DEFAULT 0,                  -- ascending sort
            updated_at  TEXT    NOT NULL DEFAULT (CURRENT_TIMESTAMP)
        );
        """,
        "CREATE INDEX IF NOT EXISTS idx_overview_widgets_pos ON overview_widgets(position);",
    };
}

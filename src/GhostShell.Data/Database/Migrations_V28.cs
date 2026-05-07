// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V28 — Scheduler simplification (Phase 71cc).
///
/// The legacy "Simple" trigger took independent <c>min_jitter_sec</c>
/// and <c>max_jitter_sec</c> fields PLUS <c>runs_per_day</c>, with
/// the gap unrelated to either the active window OR the daily target.
/// Result: with defaults (20–180s jitter, 150 runs/day, 7am–9pm) the
/// scheduler fired the full 150 quota in the first ~4 hours, then
/// went silent until tomorrow — exactly NOT what the user expects.
///
/// New model: jitter is COMPUTED automatically from
/// <c>(window_length / runs_per_day)</c>. The user only picks a single
/// boolean flag (<c>use_jitter</c>) — when true the gap is randomised
/// ±50% around the computed average; when false fires are evenly
/// spaced. Min/max columns are kept for legacy rows that haven't been
/// re-edited yet (the runner ignores them when use_jitter is set).
///
/// Two new columns:
///
///   • <c>use_jitter</c> — bool. true ⇒ random ±50%; false ⇒ uniform.
///     Default true (matches user expectation of "human-pacing").
///
///   • <c>fires_today</c> — int. Persistent daily-fire counter so the
///     cap survives app restarts. The runner increments it on every
///     fire and resets it whenever the local day rolls over.
///
///   • <c>last_fire_day</c> — TEXT (yyyy-MM-dd) marking which local
///     calendar day <c>fires_today</c> applies to. Comparing the
///     current local date against this string tells the runner
///     whether to zero the counter before the next fire.
///
/// Tolerant: ALTER TABLE … ADD COLUMN throws "duplicate column" on
/// re-run. The runner's tolerant-statement helper swallows that.
/// </summary>
internal static class Migrations_V28
{
    internal static readonly string[] Statements =
    {
        // Phase 71cc — auto-jitter flag. Default 1 = random.
        "ALTER TABLE schedules ADD COLUMN use_jitter INTEGER NOT NULL DEFAULT 1;",

        // Phase 71cc — persistent daily-fire counter.
        "ALTER TABLE schedules ADD COLUMN fires_today INTEGER NOT NULL DEFAULT 0;",

        // Phase 71cc — local calendar date the counter applies to.
        "ALTER TABLE schedules ADD COLUMN last_fire_day TEXT;",
    };
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V10 — Schedule "Simple" trigger (Phase 6.1).
///
/// Adds three columns to <c>schedules</c> for the human-pacing trigger
/// the legacy web exposed as "density mode" but we deliberately omitted
/// from V8 ("design decision to keep cron+interval only" — see
/// memory.md). Users wanted it back: cron is opaque to non-techies and
/// interval is too rigid for warmup workloads where the goal is to
/// *look* organic.
///
/// Semantics:
///   • <c>runs_per_day</c>  — soft daily target (informational + cap).
///   • <c>min_jitter_sec</c> / <c>max_jitter_sec</c> — bounds on the
///     gap between fires; the runner picks a uniform-random value in
///     [min, max] for each fire, clamped so the natural rate hits the
///     daily target as closely as the jitter range allows.
///   • Active hours (<c>active_from_hour</c> / <c>active_to_hour</c>,
///     already in V8) is the per-day window.
///
/// We do NOT add a <c>trigger_kind</c> CHECK constraint because the
/// column is free-form TEXT in V8 and adding one now would require
/// table rebuild. New value: <c>'simple'</c>.
///
/// Backfill semantics: existing rows get NULL in all three columns,
/// which the engine + UI both treat as "not configured for simple".
/// </summary>
internal static class Migrations_V10
{
    internal const string Sql = """
        ALTER TABLE schedules ADD COLUMN runs_per_day   INTEGER;
        ALTER TABLE schedules ADD COLUMN min_jitter_sec INTEGER;
        ALTER TABLE schedules ADD COLUMN max_jitter_sec INTEGER;
    """;
}

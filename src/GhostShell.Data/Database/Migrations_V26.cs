// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V26 — Auto-rotate IP per profile (Phase 71).
///
/// Adds <c>auto_rotate_ip</c> column to the profiles table. When true
/// AND the assigned proxy has a non-empty RotationUrl AND IsRotating=true,
/// the runtime hits the rotation URL BEFORE launching the browser,
/// getting a fresh IP for each session.
///
/// Default false (preserves existing behaviour for all pre-existing profiles).
/// Wrapped in the tolerant-statement path so a fresh DB created after this
/// migration doesn't error on duplicate-column attempts.
/// </summary>
internal static class Migrations_V26
{
    internal static readonly string[] Statements =
    {
        // Phase 71 — When true + proxy.IsRotating=true + proxy.RotationUrl is non-empty,
        // the runner auto-rotates before launch. Default false for backward compatibility.
        "ALTER TABLE profiles ADD COLUMN auto_rotate_ip INTEGER NOT NULL DEFAULT 0;",
    };
}

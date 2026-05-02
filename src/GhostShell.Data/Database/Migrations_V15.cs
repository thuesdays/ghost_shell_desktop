// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Data.Database;

/// <summary>
/// V15 — Per-profile ad-domain configuration (Phase 20).
///
/// Adds two CSV columns on <c>profiles</c> driving the script
/// runner's per-step domain-filter gates and the four ad-aware
/// condition kinds (<c>ad_is_mine</c> / <c>ad_is_target</c> /
/// <c>ad_is_external</c> / <c>ad_is_competitor</c>).
///
/// Both default to NULL, which makes the gates pass-through — same
/// behaviour as before this column existed. Setting either via the
/// profile dialog seeds <see cref="GhostShell.Runtime.Scripts.RunContext"/>
/// at run time.
///
/// Format (free-form CSV): "example.com, www.example.com, sub.example.com".
/// Whitespace + leading "www." stripped at load time by the runner's
/// <c>NormaliseDomain</c>.
/// </summary>
internal static class Migrations_V15
{
    internal static readonly string[] Statements =
    {
        "ALTER TABLE profiles ADD COLUMN my_domains     TEXT;",
        "ALTER TABLE profiles ADD COLUMN target_domains TEXT;",
    };
}

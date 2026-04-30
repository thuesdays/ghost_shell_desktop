// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Portable cookie bundle — an exported snapshot you can re-import
/// on a different profile. Lives in the <c>cookie_packs</c> table.
///
/// The <see cref="Slug"/> is the natural key (UNIQUE in SQL): re-
/// importing the same pack overwrites its row instead of duplicating
/// it. The PRIMARY KEY (<see cref="Id"/>) is what the API references
/// internally.
///
/// <see cref="Domains"/> is stored as a JSON array string and
/// rehydrated by the service. Surface here as a real list so the
/// UI can iterate without parsing.
///
/// Persisted payload (cookies + storage) is gzipped JSON. The size
/// difference matters: a 30-day Google pack typically has 200+
/// cookies ≈ 25 KB gzipped vs ≈ 80 KB raw, and the Cookie Packs
/// page lists dozens of them.
/// </summary>
public sealed class CookiePack
{
    public long Id { get; init; }
    public required string Slug  { get; init; }
    public required string Label { get; init; }

    /// <summary>Domains tagged on this pack (e.g. <c>["google.com", "youtube.com"]</c>).</summary>
    public IReadOnlyList<string> Domains { get; init; } = Array.Empty<string>();

    /// <summary>How "old" this profile signal looks; legacy heuristic.</summary>
    public int AgeDays { get; init; }

    /// <summary>Recent captcha rate (0..1). Lower is better.</summary>
    public double CaptchaRate { get; init; }

    public int CookiesCount { get; init; }
    public int StorageCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

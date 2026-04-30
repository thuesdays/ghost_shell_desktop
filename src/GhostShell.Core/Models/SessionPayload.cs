// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// In-memory representation of a snapshot's restoreable payload.
/// Persisted to <c>cookie_snapshots</c>'s <c>cookies_json</c> +
/// <c>storage_json</c> columns as JSON.
///
/// Held separately from <see cref="SessionSnapshot"/> (the metadata
/// row) so the Sessions list page can render thousands of rows
/// without reading the large JSON columns. Full payload is loaded
/// only when the user clicks Restore.
/// </summary>
public sealed record SessionPayload
{
    public IReadOnlyList<CookieEntry>  Cookies { get; init; } = Array.Empty<CookieEntry>();
    public IReadOnlyList<StorageEntry> Storage { get; init; } = Array.Empty<StorageEntry>();

    /// <summary>True when there's nothing worth restoring — used to
    /// short-circuit auto-save on a fresh profile that hasn't visited
    /// anywhere yet (no need to write a 0-byte snapshot row).</summary>
    public bool IsEmpty => Cookies.Count == 0 && Storage.Count == 0;

    public static SessionPayload Empty { get; } = new();
}

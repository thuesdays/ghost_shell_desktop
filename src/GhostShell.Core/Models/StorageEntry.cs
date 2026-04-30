// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Per-origin localStorage / sessionStorage capture. The browser's
/// storage API is keyed by origin (scheme + host + port), so a
/// snapshot must group entries per origin to be restoreable —
/// you can't <c>localStorage.setItem</c> for site X while you're on
/// site Y.
///
/// SessionStorage is captured for completeness but rarely useful
/// to restore (it's tab-scoped and ephemeral by design); the
/// runtime saves it but skips re-injection unless the user opts in.
/// </summary>
public sealed record StorageEntry
{
    /// <summary>Origin URL — e.g. <c>https://www.example.com</c>.</summary>
    public required string Origin { get; init; }

    /// <summary>localStorage key/value pairs. Empty dict if none.</summary>
    public IReadOnlyDictionary<string, string> LocalStorage   { get; init; }
        = new Dictionary<string, string>();

    /// <summary>sessionStorage key/value pairs. Empty dict if none.</summary>
    public IReadOnlyDictionary<string, string> SessionStorage { get; init; }
        = new Dictionary<string, string>();
}

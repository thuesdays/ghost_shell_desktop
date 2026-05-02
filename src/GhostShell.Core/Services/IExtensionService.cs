// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 27 — Browser Extensions facade.
///
/// Owns the GLOBAL extension library + per-profile overrides. Layout:
///
///   Install paths
///     • <see cref="InstallFromZipAsync"/>     — .zip / .crx archive
///     • <see cref="InstallFromFolderAsync"/>  — pre-unpacked extension dir
///     • <see cref="InstallFromStoreAsync"/>   — Chrome Web Store ID;
///                                              downloads .crx via the
///                                              public clients2.google.com
///                                              update endpoint
///
///   CRUD
///     • <see cref="ListAsync"/>               — all rows (filter by enabled)
///     • <see cref="GetAsync"/> / <see cref="GetByExtIdAsync"/>
///     • <see cref="SetGlobalEnabledAsync"/>   — flips the default for new profiles
///     • <see cref="UninstallAsync"/>          — deletes the row + the unpacked dir
///
///   Per-profile
///     • <see cref="ListEnabledForProfileAsync"/>  — what to load on Chrome start
///     • <see cref="SetEnabledForProfileAsync"/>   — explicit per-profile flip
///     • <see cref="ClearProfileOverrideAsync"/>   — fall back to global default
///
///   Store catalog
///     • <see cref="GetCuratedCatalogAsync"/>      — curated list of popular extensions
///     • <see cref="LookupStoreAsync"/>            — by ID, fetch metadata + cache
/// </summary>
public interface IExtensionService
{
    // ─── Install ───────────────────────────────────────────────────────

    /// <summary>Install from a .zip or .crx file. CRX-3 has a header
    /// before the zip payload — we strip it. Throws on parse / IO /
    /// duplicate-id failure.</summary>
    Task<ExtensionItem> InstallFromZipAsync(string archivePath, CancellationToken ct = default);

    /// <summary>Install an already-unpacked extension folder. We COPY
    /// the folder into our managed dir so the user can move/delete the
    /// original without breaking the install.</summary>
    Task<ExtensionItem> InstallFromFolderAsync(string folderPath, CancellationToken ct = default);

    /// <summary>Install from the Chrome Web Store by extension ID.
    /// Downloads the .crx via the public update endpoint, extracts,
    /// and registers. <paramref name="cwsExtId"/> is the 32-char ID
    /// from the store URL.</summary>
    Task<ExtensionItem> InstallFromStoreAsync(string cwsExtId, CancellationToken ct = default);

    // ─── CRUD ──────────────────────────────────────────────────────────

    Task<IReadOnlyList<ExtensionItem>> ListAsync(
        bool? enabledOnly = null, string? search = null,
        CancellationToken ct = default);

    Task<ExtensionItem?> GetAsync(long id, CancellationToken ct = default);
    Task<ExtensionItem?> GetByExtIdAsync(string extId, CancellationToken ct = default);

    /// <summary>Update the GLOBAL enabled flag (the default for new
    /// profiles). Existing per-profile overrides keep their explicit
    /// state.</summary>
    Task SetGlobalEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    Task SetPinnedAsync(long id, bool pinned, CancellationToken ct = default);

    /// <summary>Remove the extension's row and delete the unpacked dir.
    /// Per-profile overrides are cascaded.</summary>
    Task UninstallAsync(long id, CancellationToken ct = default);

    // ─── Per-profile overrides ─────────────────────────────────────────

    /// <summary>Returns extensions that should be loaded for the given
    /// profile, after applying per-profile overrides on top of the
    /// global defaults. The browser launcher uses this list to build
    /// the <c>--load-extension=</c> flag.</summary>
    Task<IReadOnlyList<ExtensionItem>> ListEnabledForProfileAsync(
        string profileName, CancellationToken ct = default);

    /// <summary>Returns the per-profile state for every installed
    /// extension. <c>true</c>/<c>false</c> = explicit override;
    /// <c>null</c> = inherit global. Used by the profile editor UI.</summary>
    Task<IReadOnlyDictionary<long, bool?>> GetProfileOverridesAsync(
        string profileName, CancellationToken ct = default);

    /// <summary>Set or update an explicit per-profile override.</summary>
    Task SetEnabledForProfileAsync(
        string profileName, long extensionId, bool enabled,
        CancellationToken ct = default);

    /// <summary>Drop the explicit override so the profile inherits the
    /// global default again.</summary>
    Task ClearProfileOverrideAsync(
        string profileName, long extensionId,
        CancellationToken ct = default);

    /// <summary>Cascade hook called from <c>ProfileService.DeleteAsync</c> —
    /// removes every <c>profile_extensions</c> row for the named
    /// profile so renames don't leak override state.</summary>
    Task ClearAllOverridesForProfileAsync(string profileName, CancellationToken ct = default);

    // ─── Store catalog + lookup ────────────────────────────────────────

    /// <summary>Curated list of popular extensions shipped with the app.
    /// Search filters the list locally — no network call. Each row's
    /// <see cref="ExtensionStoreEntry.ExtId"/> can be passed to
    /// <see cref="InstallFromStoreAsync"/>.</summary>
    Task<IReadOnlyList<ExtensionStoreEntry>> GetCuratedCatalogAsync(
        string? search = null, CancellationToken ct = default);

    /// <summary>Resolve a CWS extension ID to a store entry. Tries the
    /// cache first; on miss, falls back to a curated-catalog lookup.
    /// Network fetch of CWS metadata isn't reliable enough to depend on
    /// (no public API), so unknown IDs return a stub with just the ID.</summary>
    Task<ExtensionStoreEntry?> LookupStoreAsync(string cwsExtId, CancellationToken ct = default);
}

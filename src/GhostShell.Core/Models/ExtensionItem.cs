// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Phase 27 — one row in the global <c>extensions</c> library. Mirrors
/// what's installed under <c>%LocalAppData%\GhostShell\extensions</c>;
/// <see cref="LocalPath"/> points to the unpacked directory containing
/// the extension's <c>manifest.json</c>. Profiles inherit
/// <see cref="Enabled"/> as their default; the
/// <c>profile_extensions</c> table flips it per-profile when needed.
/// </summary>
public sealed record ExtensionItem
{
    public long Id { get; init; }

    /// <summary>32-character lowercase Chrome extension ID. For zip /
    /// crx / folder installs we synthesize one from a hash of the public
    /// key (or the install path if the manifest has no key) so the rest
    /// of the system has a stable reference.</summary>
    public string ExtId { get; init; } = "";

    public string Name { get; init; } = "";
    public string Version { get; init; } = "0.0.0";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? Homepage { get; init; }

    /// <summary>How the user installed it: "store" / "zip" / "crx" /
    /// "folder". Drives badge colour + "where did this come from?" UI.</summary>
    public string Source { get; init; } = "unknown";

    /// <summary>The original URL or file path the user pointed at. Useful
    /// for "Update from source" later. Null on store installs (we keep
    /// the CWS ID instead).</summary>
    public string? InstallUrl { get; init; }

    /// <summary>Absolute path to the unpacked extension folder. Chrome
    /// is launched with <c>--load-extension=&lt;LocalPath&gt;</c>.</summary>
    public string LocalPath { get; init; } = "";

    /// <summary>Raw manifest.json contents. Kept verbatim so the UI can
    /// surface fields we haven't promoted to columns yet.</summary>
    public string? ManifestJson { get; init; }

    /// <summary>Path to the icon file inside <see cref="LocalPath"/>
    /// (or null if the manifest didn't declare any). Resolved relative
    /// to the unpacked dir.</summary>
    public string? IconPath { get; init; }

    /// <summary>JSON array of permission strings declared in the manifest.
    /// Surfaced on the install dialog so the user can review what
    /// they're granting.</summary>
    public string? PermissionsJson { get; init; }

    /// <summary>JSON array of host_permissions strings (manifest v3).</summary>
    public string? HostPermissionsJson { get; init; }

    /// <summary>Default-on flag for newly created profiles. The user can
    /// flip this in the Extensions page header — affects all profiles
    /// that don't have an explicit profile_extensions row.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Toolbar pin hint forwarded to Chrome where supported.</summary>
    public bool Pinned { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>Phase 27 — per-profile override for an extension's enabled
/// state. A row's absence means "fall back to <see cref="ExtensionItem.Enabled"/>";
/// when present, the row's <see cref="Enabled"/> wins.</summary>
public sealed record ProfileExtension
{
    public long Id { get; init; }
    public string ProfileName { get; init; } = "";
    public long ExtensionId { get; init; }
    public bool Enabled { get; init; } = true;
    public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>Phase 27 — store-catalog row used for both the curated
/// catalog (shipped) and any extra rows the user discovered by pasting
/// a CWS URL (cached per fetch). Not authoritative — the canonical
/// install ends up in <see cref="ExtensionItem"/>.</summary>
public sealed record ExtensionStoreEntry
{
    public string ExtId { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string? IconUrl { get; init; }
    public double? Rating { get; init; }
    public long? Users { get; init; }
    public DateTime LastSeenAt { get; init; } = DateTime.UtcNow;
}

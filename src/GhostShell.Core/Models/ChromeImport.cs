// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Auto-discovered Chrome / Chromium-derivative install on this machine.
/// Returned by <c>IChromeImporter.DiscoverAsync</c> so the UI can render
/// a dropdown of "things you could import from".
///
/// The trio (BrandLabel, UserDataPath, ProfileFolder) uniquely
/// identifies one importable source — multiple branded browsers
/// (Chrome + Edge + Brave) on the same machine each surface as a
/// separate entry, and each in turn may have multiple profile folders
/// (Default, Profile 1, etc.).
/// </summary>
public sealed record ChromeProfileSource
{
    /// <summary>Vendor name shown to the user, e.g. "Google Chrome", "Microsoft Edge".</summary>
    public required string BrandLabel { get; init; }

    /// <summary>Path to the User Data dir (parent of "Default", "Profile 1", etc.).</summary>
    public required string UserDataPath { get; init; }

    /// <summary>Profile sub-dir name, e.g. "Default" or "Profile 1".</summary>
    public required string ProfileFolder { get; init; }

    /// <summary>
    /// Friendly profile label read from the Local State JSON
    /// (info_cache.profile_folder.name). Falls back to <see cref="ProfileFolder"/>
    /// when the JSON is unreadable.
    /// </summary>
    public string ProfileDisplayName { get; init; } = "";

    /// <summary>One-liner combining brand + profile for the dropdown.</summary>
    public string Display
        => string.IsNullOrEmpty(ProfileDisplayName)
            ? $"{BrandLabel} — {ProfileFolder}"
            : $"{BrandLabel} — {ProfileDisplayName} ({ProfileFolder})";

    /// <summary>Absolute path to the profile dir we'll read from.</summary>
    public string ProfilePath
        => System.IO.Path.Combine(UserDataPath, ProfileFolder);
}

/// <summary>
/// Knobs the user passes to the importer. Mirrors the legacy web's
/// <c>ChromeImporter</c> options surface 1:1.
/// </summary>
public sealed record ChromeImportOptions
{
    /// <summary>Source profile to read from.</summary>
    public required ChromeProfileSource Source { get; init; }

    /// <summary>Target Ghost Shell profile name. New snapshot is saved
    /// for this profile with trigger='manual', reason includes the
    /// source brand + path.</summary>
    public required string TargetProfileName { get; init; }

    /// <summary>Read cookies. If false the import is essentially "history only".</summary>
    public bool ImportCookies { get; init; } = true;

    /// <summary>How many days of history to read. 0 = no history. Default 90.</summary>
    public int HistoryDays { get; init; } = 90;

    /// <summary>Cap on history rows returned. 0 = unlimited (capped at 10000 internally).</summary>
    public int MaxUrls { get; init; } = 1000;

    /// <summary>
    /// When true, drop cookies + history entries whose URL/domain
    /// matches a curated list of sensitive markers (banking, medical,
    /// social-auth, mail, password manager). Strongly recommended.
    /// </summary>
    public bool SkipSensitiveDomains { get; init; } = true;
}

/// <summary>Outcome of a Chrome import.</summary>
public sealed record ChromeImportResult
{
    public int CookiesImported { get; init; }
    public int CookiesSkippedSensitive { get; init; }
    public int CookiesUndecryptable { get; init; }
    public int HistoryEntriesRead { get; init; }
    public int HistorySkippedSensitive { get; init; }

    /// <summary>The snapshot id we wrote (cookies live in here).</summary>
    public long? SnapshotId { get; init; }

    /// <summary>Free-form summary line for the UI.</summary>
    public string Summary { get; init; } = "";

    /// <summary>Non-fatal warnings collected during the run (file lock, missing column, etc.).</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

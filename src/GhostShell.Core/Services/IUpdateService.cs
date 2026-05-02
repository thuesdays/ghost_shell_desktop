// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Services;

public interface IUpdateService
{
    /// <summary>Hits the GitHub Releases API. Returns null if the
    /// network is unreachable or no release exists. Sets
    /// LastCheck / LastResult side-channels on the impl.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct = default);

    /// <summary>Download the portable zip into a staging dir,
    /// extract, and queue a swap-in-place + restart. Returns false
    /// when the user is already on the newest version. Reports
    /// progress 0-100 via the progress callback.</summary>
    Task<bool> ApplyAsync(UpdateInfo info, IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>True when the assembly version is older than the
    /// latest release tag. Settable so the UI can clear after the
    /// user dismisses the banner without rerunning the check.</summary>
    bool UpdateAvailable { get; }
    UpdateInfo? LatestKnown { get; }
    event EventHandler<UpdateInfo>? UpdateFound;

    /// <summary>Fired right after ApplyAsync hands off to the
    /// PowerShell helper. The App layer subscribes to this and
    /// calls <c>Application.Current.Shutdown(0)</c> — keeping the
    /// WPF dependency out of the Data project (which doesn't
    /// reference PresentationFramework). The PowerShell script
    /// waits on the parent PID, so a clean WPF shutdown is what
    /// unblocks the file swap.</summary>
    event EventHandler? ShutdownRequested;
}

public sealed record UpdateInfo
{
    public required Version LatestVersion { get; init; }
    public required Version CurrentVersion { get; init; }
    public required string TagName { get; init; }       // "v0.0.2.0"
    public required string ReleaseName { get; init; }   // "Ghost Shell 0.0.2.0"
    public required string ReleaseNotes { get; init; }  // markdown body
    public required DateTime PublishedAt { get; init; }
    /// <summary>URL for the portable .zip ("apply" path).</summary>
    public string? PortableZipUrl { get; init; }
    /// <summary>URL for GhostShellDesktopSetup.exe ("download installer" path).</summary>
    public string? InstallerExeUrl { get; init; }
    /// <summary>Public Releases page (fallback for both buttons).</summary>
    public required string ReleasePageUrl { get; init; }
}

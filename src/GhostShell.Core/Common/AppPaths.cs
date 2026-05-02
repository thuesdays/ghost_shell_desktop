// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Common;

/// <summary>
/// Single source of truth for where Ghost Shell keeps its data on
/// disk. Mirrors the legacy `core/platform_paths.py` but uses
/// %LocalAppData%\GhostShell\ as the root by default — the desktop
/// app is per-user and never writes to Program Files at runtime.
///
/// Resolution can be overridden by env vars, useful for tests and
/// side-by-side runs against the legacy installation:
///   GHOSTSHELL_DATA_DIR        — root data dir (db, profiles, logs)
///   GHOSTSHELL_CHROMIUM_DIR    — patched-Chromium folder override
/// </summary>
public static class AppPaths
{
    private const string EnvDataDir       = "GHOSTSHELL_DATA_DIR";
    private const string EnvChromiumDir   = "GHOSTSHELL_CHROMIUM_DIR";

    /// <summary>The root data directory. Created on first access.</summary>
    public static string DataDir
    {
        get
        {
            var envOverride = Environment.GetEnvironmentVariable(EnvDataDir);
            var path = !string.IsNullOrWhiteSpace(envOverride)
                ? envOverride
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GhostShell");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DatabasePath  => Path.Combine(DataDir, "ghost_shell.db");
    public static string LogsDir       => EnsureDir(Path.Combine(DataDir, "logs"));
    public static string ProfilesDir   => EnsureDir(Path.Combine(DataDir, "profiles"));
    public static string VaultPath     => Path.Combine(DataDir, "vault.enc");
    public static string SettingsPath  => Path.Combine(DataDir, "app.json");

    /// <summary>Phase 27 — root for unpacked extensions. Each extension
    /// lives under <c>extensions/&lt;ext_id&gt;/</c> and is referenced
    /// by Chrome via <c>--load-extension=&lt;path&gt;</c>.</summary>
    public static string ExtensionsDir => EnsureDir(Path.Combine(DataDir, "extensions"));

    /// <summary>Path to the unpacked dir for a single extension. Throws
    /// on a non-32-char id (defence against ../path-traversal).</summary>
    public static string ExtensionDir(string extId)
    {
        if (string.IsNullOrWhiteSpace(extId) || extId.Length != 32 ||
            !extId.All(c => c is >= 'a' and <= 'p'))
            throw new ArgumentException(
                "extension id must be 32 chars in [a-p]",
                nameof(extId));
        return EnsureDir(Path.Combine(ExtensionsDir, extId));
    }

    /// <summary>
    /// Per-profile user-data-dir. Chromium passes this via
    /// `--user-data-dir=...` and stores cookies / cache / extensions
    /// state inside. Created lazily on first profile launch.
    /// </summary>
    public static string ProfileDir(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name required.", nameof(profileName));
        return EnsureDir(Path.Combine(ProfilesDir, profileName));
    }

    /// <summary>
    /// Optional override for the patched-Chromium directory. When
    /// set, <see cref="ChromiumLocator"/> probes this path first
    /// before falling back to the install-relative chain.
    /// </summary>
    public static string? ChromiumDirOverride =>
        Environment.GetEnvironmentVariable(EnvChromiumDir);

    private static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

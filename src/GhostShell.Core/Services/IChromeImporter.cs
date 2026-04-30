// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Reads cookies (+ optionally history) out of a real Chrome /
/// Chromium-based browser profile dir on this machine and persists
/// them as a Ghost Shell snapshot for the selected profile.
///
/// Why: the warmup robot can build identity over time, but a
/// genuinely-aged Chrome profile is unbeatable as a starting state —
/// 30+ days of cookies, real localStorage, history-driven trust
/// signals at major sites. Importing the user's own Chrome profile
/// (after an honest sensitive-domain filter) is a one-shot way to
/// jump-start a fresh Ghost Shell profile.
///
/// Threading: every call is async + cooperative — the importer copies
/// the source SQLite files to a temp dir before reading (Chrome holds
/// a write lock when running) and tears them down on exit.
/// </summary>
public interface IChromeImporter
{
    /// <summary>
    /// Auto-discover every Chromium-based install on this machine.
    /// Returns one entry per profile-dir per browser. Empty list is
    /// legitimate ("nothing on disk yet"). Never throws — disk I/O
    /// errors degrade to warnings logged via the injected logger.
    /// </summary>
    Task<IReadOnlyList<ChromeProfileSource>> DiscoverAsync(CancellationToken ct = default);

    /// <summary>
    /// Run the import. Reads <paramref name="opts"/>.Source's Cookies +
    /// Login Data DBs, decrypts via DPAPI/AES-GCM (Win10+), filters
    /// sensitive domains if requested, optionally reads history, and
    /// writes a snapshot for <paramref name="opts"/>.TargetProfileName.
    ///
    /// Throws <see cref="System.IO.FileNotFoundException"/> if the
    /// source DB doesn't exist; throws
    /// <see cref="InvalidOperationException"/> if running on non-Windows
    /// (DPAPI is OS-bound).
    /// </summary>
    Task<ChromeImportResult> ImportAsync(
        ChromeImportOptions opts, CancellationToken ct = default);
}

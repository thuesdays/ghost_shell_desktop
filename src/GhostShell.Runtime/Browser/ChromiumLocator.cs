// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using GhostShell.Core.Common;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Walks a deterministic candidate list to find the patched
/// Chromium build. First match wins. The candidate order matters:
///
///   1. Env-var override (<c>GHOSTSHELL_CHROMIUM_DIR</c>) — for tests / CI
///   2. <c>&lt;install&gt;\chromium\</c> — production layout from installer
///   3. <c>&lt;install&gt;\chrome_win64\</c> — alternate name some builds use
///   4. Legacy ghost_shell_browser project's chrome_win64 — dev fallback
///   5. Raw Chromium build output (out\GhostShell, out\Default) — Chromium devs
///
/// Each candidate must contain BOTH <c>chrome.exe</c> and
/// <c>chromedriver.exe</c>, otherwise we keep walking.
/// </summary>
public sealed class ChromiumLocator : IChromiumLocator
{
    private readonly ILogger<ChromiumLocator> _log;

    public ChromiumLocator(ILogger<ChromiumLocator> log) => _log = log;

    public ChromiumStatus Locate()
    {
        var tried = new List<string>();
        foreach (var (dir, source) in BuildCandidates())
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            tried.Add($"{source}: {dir}");

            var chrome = Path.Combine(dir, "chrome.exe");
            var driver = Path.Combine(dir, "chromedriver.exe");

            if (!File.Exists(chrome)) continue;
            if (!File.Exists(driver))
            {
                _log.LogWarning(
                    "Chromium found at {Dir} but chromedriver.exe is missing — skipping",
                    dir);
                continue;
            }

            var version = ReadVersion(chrome);
            _log.LogInformation(
                "Chromium located: {Source} → {Path} (v{Version})",
                source, chrome, version ?? "?");

            return new ChromiumStatus
            {
                Found            = true,
                ChromePath       = chrome,
                ChromeDriverPath = driver,
                VersionString    = version,
                ProbedFrom       = source,
                Candidates       = tried,
            };
        }

        _log.LogError(
            "Chromium NOT found. Tried {Count} candidates: {Candidates}",
            tried.Count, string.Join(" | ", tried));

        return new ChromiumStatus
        {
            Found      = false,
            Candidates = tried,
            Error      = "Patched Chromium build not found. Set GHOSTSHELL_CHROMIUM_DIR " +
                         "or place chromium\\ next to GhostShell.exe.",
        };
    }

    private static IEnumerable<(string Dir, string Source)> BuildCandidates()
    {
        // 1. env override — wins everything
        var env = AppPaths.ChromiumDirOverride;
        if (!string.IsNullOrWhiteSpace(env))
            yield return (env, "env GHOSTSHELL_CHROMIUM_DIR");

        // 2-3. siblings of GhostShell.exe (production install)
        var asmDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(asmDir))
        {
            yield return (Path.Combine(asmDir, "chromium"),       "<install>\\chromium");
            yield return (Path.Combine(asmDir, "chrome_win64"),   "<install>\\chrome_win64");
        }

        // 4. legacy ghost_shell_browser project fallback (dev machines)
        yield return (@"F:\projects\ghost_shell_browser\chrome_win64",
                      "legacy ghost_shell_browser");

        // 5. raw Chromium build output (Chromium devs working on patches)
        yield return (@"F:\projects\chromium\src\out\GhostShell",
                      "Chromium build output (F:)");
        yield return (@"C:\src\chromium\src\out\GhostShell",
                      "Chromium build output (C:)");
        yield return (@"C:\src\chromium\src\out\Default",
                      "Chromium build output Default (C:)");
    }

    private static string? ReadVersion(string exePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return info.ProductVersion ?? info.FileVersion;
        }
        catch
        {
            return null;
        }
    }
}

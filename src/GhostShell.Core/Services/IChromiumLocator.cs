// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Services;

/// <summary>
/// Finds the patched Chromium build on disk + matching chromedriver.
/// The desktop installer ships them under <c>&lt;install&gt;\chromium\</c>;
/// developer environments fall back to the legacy ghost_shell_browser
/// project's <c>chrome_win64</c> directory.
/// </summary>
public interface IChromiumLocator
{
    /// <summary>Probe known locations and return the result.</summary>
    ChromiumStatus Locate();
}

/// <summary>
/// Outcome of a Chromium probe. <see cref="Found"/> false means we
/// failed every candidate path — UI should surface that and keep
/// the runtime in stub-mode until the user fixes installation.
/// </summary>
public sealed class ChromiumStatus
{
    public bool Found { get; init; }
    public string? ChromePath { get; init; }
    public string? ChromeDriverPath { get; init; }

    /// <summary>Human-readable version string from chrome.exe (e.g. "149.0.7805.0").</summary>
    public string? VersionString { get; init; }

    /// <summary>Friendly name of the candidate that matched (for the Settings UI).</summary>
    public string? ProbedFrom { get; init; }

    /// <summary>List of every candidate we tried, in order. Useful for "not found" diagnostics.</summary>
    public IReadOnlyList<string> Candidates { get; init; } = Array.Empty<string>();

    public string? Error { get; init; }
}

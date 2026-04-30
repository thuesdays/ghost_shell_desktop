// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.App.ViewModels;

namespace GhostShell.App.Navigation;

/// <summary>
/// Resolves a page key (e.g. "overview", "profiles") to the matching
/// view-model. The shell's content-area binding listens to
/// <see cref="CurrentChanged"/> and swaps the view template.
/// </summary>
public interface INavigationService
{
    BaseViewModel? Current { get; }
    string? CurrentKey { get; }

    event EventHandler? CurrentChanged;

    /// <summary>Navigate to a registered page. Unknown key → no-op + warning log.</summary>
    void NavigateTo(string pageKey);
}

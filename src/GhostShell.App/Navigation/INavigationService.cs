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

    /// <summary>Phase 71v — true when the back-stack is non-empty, i.e.
    /// the user got here from another page via an in-page deep link
    /// (Overview tile, "View all runs" button, etc.) rather than via
    /// the sidebar. Drives the visibility of the Back chip in
    /// MainWindow's header.</summary>
    bool CanGoBack { get; }

    /// <summary>Phase 71v — Navigate to a registered page. Unknown key → no-op + warning log.
    /// When <paramref name="pushHistory"/> is true, the current page's
    /// key is pushed onto the back-stack so <see cref="GoBack"/> can
    /// return to it. Sidebar clicks pass false (they're "root nav" —
    /// pressing Profiles from anywhere shouldn't surface a Back chip
    /// pointing at wherever you happened to be). In-page deep links
    /// pass true.</summary>
    void NavigateTo(string pageKey, bool pushHistory = false);

    /// <summary>Phase 71v — pop the top of the back-stack and navigate
    /// to it. No-op when the stack is empty.</summary>
    void GoBack();
}

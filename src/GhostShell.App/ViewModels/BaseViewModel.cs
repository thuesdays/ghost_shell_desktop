// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using CommunityToolkit.Mvvm.ComponentModel;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Common base. <see cref="OnNavigatedToAsync"/> fires every time
/// the page becomes visible — overriding pages reload data here so
/// the user always sees fresh state on tab switch.
/// </summary>
public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

    /// <summary>
    /// Called when the user navigates AWAY from this page. Override
    /// to stop timers / paused-tail / other live-update plumbing
    /// that would otherwise keep ticking against an off-screen VM.
    /// </summary>
    public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
}

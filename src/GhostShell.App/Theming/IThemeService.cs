// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.App.Theming;

/// <summary>Phase 71aa — UI theme variants. Persisted as a lowercase
/// string ("dark" / "light") via SettingsKeys.UiTheme so the field
/// stays human-editable in app_settings without enum-int coupling.</summary>
public enum AppTheme
{
    Dark,
    Light,
}

/// <summary>Phase 71aa — owns the active palette + persistence.
/// At startup, App.xaml.cs calls <see cref="ApplySaved"/> BEFORE the
/// main window is constructed so all StaticResource references in
/// XAML resolve against whichever palette the user last picked.
///
/// Live theme switching is NOT supported — every brush in the app is
/// referenced via <c>{StaticResource}</c> which bakes the colour at
/// parse time. The Settings page therefore prompts a restart when the
/// user changes the theme; <see cref="SaveAsync"/> persists the choice
/// so the next launch picks it up.</summary>
public interface IThemeService
{
    /// <summary>The theme that was loaded at app startup. Whatever
    /// the Settings tab displays as "current".</summary>
    AppTheme Active { get; }

    /// <summary>Read the saved theme from <c>SettingsKeys.UiTheme</c>
    /// and rewrite Colors.xaml's first MergedDictionaries[].Source so
    /// the right palette is in scope before any window is parsed.
    /// Default is Dark when the key is missing or unrecognised.</summary>
    Task ApplySavedAsync(CancellationToken ct = default);

    /// <summary>Persist the user's choice. Does NOT switch the live
    /// palette (StaticResources can't update); callers should follow
    /// up with a restart prompt.</summary>
    Task SaveAsync(AppTheme theme, CancellationToken ct = default);
}

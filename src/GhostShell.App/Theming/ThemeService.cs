// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Theming;

/// <summary>Phase 71aa — theme service backed by ISettingsService.
/// On <see cref="ApplySavedAsync"/> we walk the application-resource
/// tree to find Colors.xaml (the palette loader), then rewrite its
/// inner <c>MergedDictionaries[0].Source</c> to point at either
/// Palette.Dark.xaml or Palette.Light.xaml. The Source rewrite must
/// happen BEFORE any Window's XAML is parsed — at parse time WPF
/// resolves every <c>{StaticResource BgBase}</c> against whichever
/// palette is currently merged in, and the resolved Brush is then
/// baked into the Style/ControlTemplate. Subsequent Source rewrites
/// are no-ops for already-resolved StaticResource consumers, which
/// is why the Settings page asks the user to restart on change.</summary>
internal sealed class ThemeService : IThemeService
{
    private readonly ISettingsService _settings;
    private readonly ILogger<ThemeService> _log;

    public AppTheme Active { get; private set; } = AppTheme.Dark;

    public ThemeService(ISettingsService settings, ILogger<ThemeService> log)
    {
        _settings = settings;
        _log = log;
    }

    public async Task ApplySavedAsync(CancellationToken ct = default)
    {
        var raw = await _settings.GetStringAsync(SettingsKeys.UiTheme, ct);
        var theme = ParseTheme(raw);
        ApplyToResources(theme);
        Active = theme;
        _log.LogInformation("Theme.ApplySaved: raw='{Raw}', resolved={Theme}", raw ?? "(null)", theme);
    }

    public async Task SaveAsync(AppTheme theme, CancellationToken ct = default)
    {
        await _settings.SetStringAsync(SettingsKeys.UiTheme, theme.ToString().ToLowerInvariant(), ct);
        _log.LogInformation("Theme.Save: persisted {Theme} (restart required to apply)", theme);
    }

    private static AppTheme ParseTheme(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AppTheme.Dark;
        if (string.Equals(raw, "light", StringComparison.OrdinalIgnoreCase)) return AppTheme.Light;
        return AppTheme.Dark;
    }

    /// <summary>Walk Application.Resources.MergedDictionaries → find
    /// Colors.xaml (the loader) by URI suffix → rewrite its inner
    /// MergedDictionaries[0].Source to the chosen palette. Done with
    /// nested loops because WPF gives us no nicer "find by source"
    /// API. Failure here is non-fatal — the app keeps running with
    /// whichever palette was the default in Colors.xaml.</summary>
    private void ApplyToResources(AppTheme theme)
    {
        var paletteUri = theme switch
        {
            AppTheme.Light => new Uri("pack://application:,,,/Resources/Themes/Palette.Light.xaml"),
            _              => new Uri("pack://application:,,,/Resources/Themes/Palette.Dark.xaml"),
        };

        var app = Application.Current;
        if (app is null)
        {
            _log.LogWarning("Theme: Application.Current is null — can't apply palette.");
            return;
        }

        var colorsDict = FindColorsDictionary(app.Resources);
        if (colorsDict is null)
        {
            _log.LogWarning(
                "Theme: Colors.xaml loader not found in Application.Resources tree — palette cannot be swapped.");
            return;
        }

        if (colorsDict.MergedDictionaries.Count == 0)
        {
            _log.LogWarning("Theme: Colors.xaml has no inner MergedDictionaries — nothing to rewrite.");
            return;
        }

        // Rewrite the inner source. Note: assigning .Source forces WPF
        // to reload the dict from the new pack URI synchronously. Brush
        // keys with the same x:Key inside the new palette replace the
        // old ones, so any subsequent StaticResource lookup picks up
        // the new values.
        var inner = colorsDict.MergedDictionaries[0];
        var oldSrc = inner.Source?.OriginalString ?? "(null)";
        inner.Source = paletteUri;
        _log.LogInformation(
            "Theme: palette swapped {Old} -> {New}", oldSrc, paletteUri.OriginalString);
    }

    private static ResourceDictionary? FindColorsDictionary(ResourceDictionary root)
    {
        // Recursive depth-first: the loader is nested under DarkTheme.xaml
        // which itself is merged at App.Resources. Walking the tree
        // catches it regardless of where it sits.
        foreach (var dict in root.MergedDictionaries)
        {
            if (IsColorsLoader(dict)) return dict;
            var nested = FindColorsDictionary(dict);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static bool IsColorsLoader(ResourceDictionary dict)
    {
        var src = dict.Source?.OriginalString;
        if (string.IsNullOrEmpty(src)) return false;
        // Match either "Colors.xaml" anywhere in the path (relative ref
        // from DarkTheme.xaml) or the absolute pack form. Case-insensitive
        // because WPF's source URIs are case-insensitive on Windows.
        return src.IndexOf("Colors.xaml", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

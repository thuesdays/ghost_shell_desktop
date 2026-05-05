// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GhostShell.App.Converters;

/// <summary>
/// Phase 70 — boolean inverter (true → false, false → true).
/// Used by the Competitors leaderboard's Target/Block action
/// buttons: <c>IsEnabled="{Binding IsInTarget, Converter=...}"</c>
/// disables the "Add to Target" button once the domain is already
/// classified, so the user can't double-click and produce a duplicate
/// (the underlying service de-dupes too, but the UX is nicer if the
/// button visibly greys out).
///
/// Lives next to the Visibility-flavoured inverter
/// (<see cref="InverseBoolToVisibilityConverter"/>) but returns
/// <see cref="bool"/>, not <see cref="Visibility"/>, so it can drive
/// any bool dependency property (IsEnabled, IsChecked, IsHitTestVisible…).
/// Null is treated as <c>false</c> so the binding still flips to <c>true</c>.
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : Binding.DoNothing;
}

/// <summary>
/// null → Collapsed, anything else → Visible. Used by the Logs page
/// filter chips: a chip for "level filter" should hide when no
/// level is selected (LevelFilter is null) and show with the
/// current value otherwise.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Non-empty string → Visible, null/empty/whitespace → Collapsed.
/// Used for filter chips bound to free-text fields (SourceFilter,
/// SearchText, ProfileFilter): the chip shows the current value
/// only when the user has actually typed something.
/// </summary>
public sealed class NonEmptyToVisibleConverter : IValueConverter
{
    /// <summary>
    /// Phase 70 — accept three input shapes:
    ///   • string  → Visible iff non-whitespace (original semantics)
    ///   • int/long → Visible iff > 0 (lets {Binding Items.Count} work)
    ///   • ICollection / IEnumerable → Visible iff has any element
    /// Anything else (null, false, …) → Collapsed.
    ///
    /// Was previously string-only; CompetitorsView binds the converter
    /// to <c>{Binding LeaderboardRows.Count}</c> (an int) for grid
    /// visibility, which always evaluated false → the leaderboard
    /// DataGrid stayed Collapsed even when the collection had items
    /// (footer "3 competitor(s)" rendered, grid above did not).
    /// </summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            string s when !string.IsNullOrWhiteSpace(s) => Visibility.Visible,
            int  i when i > 0                            => Visibility.Visible,
            long l when l > 0                            => Visibility.Visible,
            System.Collections.ICollection col when col.Count > 0 => Visibility.Visible,
            System.Collections.IEnumerable en when HasAny(en)     => Visibility.Visible,
            _ => Visibility.Collapsed,
        };
    }

    private static bool HasAny(System.Collections.IEnumerable en)
    {
        var e = en.GetEnumerator();
        try { return e.MoveNext(); }
        finally { (e as IDisposable)?.Dispose(); }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

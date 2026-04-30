// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GhostShell.App.Converters;

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
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

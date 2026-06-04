// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GhostShell.App.Converters;

/// <summary>
/// Redesign 2026-06 — maps a short icon KEY (e.g. "Profiles") to the
/// matching vector <see cref="Geometry"/> resource declared in
/// <c>Resources/Themes/Icons.xaml</c> as <c>Icon.Profiles</c>. Lets
/// data-bound icons (sidebar nav rows, whose key lives on the view-model)
/// render through the <see cref="Controls.Icon"/> control without the
/// view-model taking a hard dependency on WPF geometry types.
///
/// Unknown / empty keys resolve to null (the Icon control simply draws
/// nothing) so a missing mapping degrades gracefully rather than throwing.
/// </summary>
public sealed class IconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key)) return null;
        return Application.Current?.TryFindResource("Icon." + key) as Geometry;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

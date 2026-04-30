// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GhostShell.App.Converters;

/// <summary>
/// Resolve a resource-dictionary key (string) to the actual
/// <see cref="Brush"/> registered under it. Used by the Profile-card
/// accent stripe — the VM emits a key like "HueTeal" and the View
/// looks the brush up dynamically. Without this converter we'd have
/// to either generate per-card styles or expose Brush instances on
/// the VM (which couples the VM to System.Windows.Media — bad layering).
/// </summary>
public sealed class ResourceKeyToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string key || string.IsNullOrEmpty(key))
            return Brushes.Transparent;
        var found = Application.Current?.TryFindResource(key);
        return found as Brush ?? (Brush)Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

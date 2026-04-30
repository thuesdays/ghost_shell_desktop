// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using GhostShell.Core.Models;

namespace GhostShell.App.Converters;

/// <summary>
/// Maps a <see cref="Run"/>'s state to the matching pill style:
///   • running → PillWarn   (amber, "running")
///   • success → PillOk     (green, "OK")
///   • failure → PillErr    (red,   exit code)
///   • unknown → PillNeutral
///
/// Returned as a <see cref="Style"/> so the consuming Border can
/// apply it directly via <c>Style="{Binding ...}"</c>.
/// </summary>
public sealed class RunToPillStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Run r) return GetStyle("PillNeutral");
        if (r.IsRunning) return GetStyle("PillWarn");
        if (r.IsSuccess) return GetStyle("PillOk");
        if (r.IsFailed)  return GetStyle("PillErr");
        return GetStyle("PillNeutral");
    }

    private static object GetStyle(string key)
    {
        if (Application.Current?.TryFindResource(key) is Style s) return s;
        return DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Same mapping as <see cref="RunToPillStyleConverter"/> but returns
/// the foreground colour for the pill text. Pills use a soft tinted
/// background + a coloured border + matching text; the border style
/// covers bg/border, this covers the text.
/// </summary>
public sealed class RunToPillForegroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Run r) return GetBrush("Text");
        if (r.IsRunning) return GetBrush("WarnBrush");
        if (r.IsSuccess) return GetBrush("OkBrush");
        if (r.IsFailed)  return GetBrush("ErrBrush");
        return GetBrush("Text");
    }

    private static object GetBrush(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

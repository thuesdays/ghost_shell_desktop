// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GhostShell.App.Converters;

/// <summary>true → Visible, false → Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>true → Collapsed, false → Visible. Use when you want to hide on truthy.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Collapsed;
}

/// <summary>Phase 30 — string-equals visibility converter. Returns
/// Visible when the bound value matches the ConverterParameter
/// string, Collapsed otherwise. Used by the Settings tabbed view to
/// show only the active tab's section.</summary>
public sealed class StringEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string, StringComparison.Ordinal)
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Phase 30 — string-equals → bool for a SINGLE binding.
/// Use the parameter form: <c>{Binding ActiveTab, Converter=…, ConverterParameter=performance}</c>.
///
/// Phase 34 — converter now compares by ToString(InvariantCulture)
/// so int / enum / string sources all work. <see cref="ConvertBack"/>
/// returns the parameter coerced to the binding's target type when
/// the toggle becomes checked, and <see cref="Binding.DoNothing"/>
/// when it becomes unchecked — that's how a row of mutually-exclusive
/// ToggleButtons can drive a single int property like Days
/// (1/7/30/0) without hitting NotSupportedException on every click.
/// </summary>
public sealed class StringEqualsBoolConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.Equals(AsInvariantString(value), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Toggle was unchecked: keep the source value as-is (don't
        // clear the bound int because the user clicked an already-
        // active toggle). DoNothing is WPF's "skip this update".
        if (value is not true) return Binding.DoNothing;
        if (parameter is null)  return Binding.DoNothing;
        var raw = parameter.ToString() ?? "";
        // Coerce to the binding's target type. Days is bound as int,
        // but the same converter is reused on string properties
        // (ActiveTab) where the parameter goes through unchanged.
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t == typeof(string)) return raw;
        try { return System.Convert.ChangeType(raw, t, CultureInfo.InvariantCulture); }
        catch { return Binding.DoNothing; }
    }

    /// <summary>Multi-binding form: takes [activeTab, rowId] and returns
    /// true when they match. Used by the Settings rail's active-row
    /// highlight where the row id lives on the bound DataContext and
    /// the active id lives on the parent VM. Without this overload
    /// XAML rejects the converter at parse time with
    /// "Unable to cast … to IMultiValueConverter".</summary>
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values is null || values.Length < 2) return false;
        return string.Equals(AsInvariantString(values[0]), AsInvariantString(values[1]), StringComparison.Ordinal);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static string? AsInvariantString(object? v)
    {
        if (v is null) return null;
        if (v is string s) return s;
        return System.Convert.ToString(v, CultureInfo.InvariantCulture);
    }
}

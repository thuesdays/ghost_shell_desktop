// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows.Data;

namespace GhostShell.App.Converters;

/// <summary>
/// Two-way string-equals converter. Used to wire RadioButtons to a
/// single string property: each RadioButton's <c>IsChecked</c> binds
/// to the property with <c>ConverterParameter="value"</c>; toggling
/// it on writes that value back, and the RadioButton lights up when
/// the property currently equals that value.
/// </summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.Equals(value as string, parameter as string,
                         StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        // Only when the radio is being checked (true) do we want to
        // write the parameter back. False (radio unchecked) shouldn't
        // do anything — the OTHER radio's true write will overwrite us.
        => value is bool b && b
            ? parameter
            : System.Windows.Data.Binding.DoNothing;
}

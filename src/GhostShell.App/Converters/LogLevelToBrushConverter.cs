// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GhostShell.App.Converters;

/// <summary>
/// Maps a Serilog short level tag (TRC/DBG/INF/WAR/ERR/FTL/RAW) to
/// the matching theme Brush. Used inside the Logs page row template
/// where the level value lives on a <c>Run</c> element — Run isn't
/// a FrameworkElement, so it can't hang DataTriggers off itself.
/// A converter is the cleanest way to colour both the level tag and
/// the message text in one shot.
/// </summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    /// <summary>
    /// When true, treats Information/Trace/Debug as the default
    /// foreground (so the message reads as plain text). When false,
    /// returns the level-coloured brush even for INF (used for the
    /// short level chip itself).
    /// </summary>
    public bool DimNormalLevels { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value as string ?? "INF";
        var key = level switch
        {
            "ERR" or "FTL" => "ErrBrush",
            "WAR"          => "WarnBrush",
            "INF"          => DimNormalLevels ? "Text"     : "OkBrush",
            "DBG"          => DimNormalLevels ? "Text"     : "TextMuted",
            "TRC"          => DimNormalLevels ? "TextDim"  : "TextMuted",
            "RAW"          => "TextMuted",
            _              => "Text",
        };
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Brushes.White;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

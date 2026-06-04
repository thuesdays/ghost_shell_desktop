// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GhostShell.App.Controls;

/// <summary>
/// Redesign 2026-06 — lightweight vector icon primitive. Renders a
/// stroke-based ("Lucide"-style) <see cref="Geometry"/> authored on a
/// 24×24 grid, scaled uniformly to <see cref="Size"/> through a Viewbox
/// in the control template (see <c>Resources/Themes/Icons.xaml</c>).
///
/// Replaces the old approach of <c>&lt;TextBlock FontFamily="Segoe MDL2
/// Assets" Text="&amp;#xE7xx;"/&gt;</c> glyphs: vector paths stay razor
/// sharp at any DPI / scale, use one consistent visual language, and
/// recolour via <see cref="Stroke"/> (per-page hue) without depending on
/// a Win10-only icon font being installed.
///
/// Usage: <c>&lt;ctrl:Icon Data="{StaticResource Icon.Profiles}"
/// Stroke="{DynamicResource HueGreen}" Size="18"/&gt;</c>.
/// </summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Icon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SizeProperty =
        DependencyProperty.Register(nameof(Size), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(18.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Path geometry authored on the 24×24 Lucide grid.</summary>
    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    /// <summary>Stroke brush. Defaults (via the template) to the theme text colour.</summary>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>Rendered edge length in DIPs (square). Default 18.</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Stroke width in the 24×24 source space (scaled with the icon). Default 2.</summary>
    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }
}

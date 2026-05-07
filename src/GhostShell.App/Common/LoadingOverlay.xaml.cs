// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;

namespace GhostShell.App.Common;

/// <summary>
/// Phase 71n — reusable busy overlay. Bind <see cref="IsActive"/> to a
/// view-model's <c>IsBusy</c> flag and the overlay covers its parent
/// cell with a translucent backdrop, a centred spinner, and a
/// configurable caption + sub-caption.
///
/// Designed to drop on top of long-loading lists (Profiles with 500+
/// rows, Groups page hydrating row VMs, scripts apply-to-many flow).
/// Cost when inactive: zero — the root collapses to nothing and
/// IsHitTestVisible=false so it doesn't even intercept mouse events.
/// </summary>
public partial class LoadingOverlay : UserControl
{
    public LoadingOverlay()
    {
        InitializeComponent();
    }

    /// <summary>True → overlay visible + hit-testable (blocks input);
    /// false → invisible + transparent to clicks. Bind to your VM's
    /// <c>IsBusy</c>.</summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(LoadingOverlay),
            new PropertyMetadata(false));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>Primary status line — e.g. "Loading profiles…".</summary>
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(
            nameof(Caption), typeof(string), typeof(LoadingOverlay),
            new PropertyMetadata("Loading…"));

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    /// <summary>Optional secondary line — e.g. "127 of 500 rows".
    /// Hidden when null/empty.</summary>
    public static readonly DependencyProperty SubcaptionProperty =
        DependencyProperty.Register(
            nameof(Subcaption), typeof(string), typeof(LoadingOverlay),
            new PropertyMetadata(null, OnSubcaptionChanged));

    public string? Subcaption
    {
        get => (string?)GetValue(SubcaptionProperty);
        set => SetValue(SubcaptionProperty, value);
    }

    /// <summary>Read-only convenience for the sub-caption visibility
    /// binding inside the XAML — the binding can't easily check
    /// "string is non-empty" without a converter, so we expose this
    /// derived bool and re-fire it whenever Subcaption changes.</summary>
    public static readonly DependencyProperty HasSubcaptionProperty =
        DependencyProperty.Register(
            nameof(HasSubcaption), typeof(bool), typeof(LoadingOverlay),
            new PropertyMetadata(false));

    public bool HasSubcaption
    {
        get => (bool)GetValue(HasSubcaptionProperty);
        private set => SetValue(HasSubcaptionProperty, value);
    }

    private static void OnSubcaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LoadingOverlay self)
            self.HasSubcaption = !string.IsNullOrWhiteSpace(e.NewValue as string);
    }
}

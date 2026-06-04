// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Media;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Generic confirm / alert modal. Severity drives:
///   • left accent strip colour,
///   • header icon glyph + colour,
///   • confirm-button style (primary blue vs red for destructive /
///     error variants).
/// </summary>
public partial class ConfirmDialog : Window
{
    // Redesign 2026-06 — vector icon KEYS (resolved to a Geometry from
    // Icons.xaml) instead of Segoe MDL2 Assets glyph codepoints.
    private const string IconInfo    = "Info";
    private const string IconSuccess = "Success";
    private const string IconWarn    = "Warning";
    private const string IconError   = "Error";
    private const string IconDanger  = "Warning"; // matches "are you sure" tone

    public ConfirmDialog(
        string title, string message,
        string confirmLabel = "Confirm",
        ConfirmSeverity severity = ConfirmSeverity.Neutral)
    {
        InitializeComponent();
        TitleText.Text     = title;
        MessageText.Text   = message;
        ConfirmBtn.Content = confirmLabel;

        ApplySeverity(severity);

        // For pure alerts (single-button "OK") hide the Cancel button —
        // the close-X still works for cancel intent.
        if (string.Equals(confirmLabel, "OK", StringComparison.OrdinalIgnoreCase))
            CancelBtn.Visibility = Visibility.Collapsed;
    }

    private void ApplySeverity(ConfirmSeverity severity)
    {
        Brush  brush;
        string iconKey;
        bool   dangerCta = false;

        switch (severity)
        {
            case ConfirmSeverity.Info:
                brush = (Brush)FindResource("InfoBrush");
                iconKey = IconInfo;
                break;
            case ConfirmSeverity.Success:
                brush = (Brush)FindResource("OkBrush");
                iconKey = IconSuccess;
                break;
            case ConfirmSeverity.Warning:
                brush = (Brush)FindResource("WarnBrush");
                iconKey = IconWarn;
                break;
            case ConfirmSeverity.Error:
                brush = (Brush)FindResource("ErrBrush");
                iconKey = IconError;
                dangerCta = true;
                break;
            case ConfirmSeverity.Danger:
                // Header neutral; only confirm button goes red — signals
                // "this is destructive but the user is choosing it" vs
                // "system-error report".
                brush = (Brush)FindResource("Accent");
                iconKey = IconDanger;
                dangerCta = true;
                break;
            default:
                brush = (Brush)FindResource("Accent");
                iconKey = IconInfo;
                break;
        }

        AccentStrip.Background = brush;
        HeaderIcon.Stroke = brush;
        HeaderIcon.Data   = Application.Current?.TryFindResource("Icon." + iconKey) as Geometry;

        if (dangerCta)
            ConfirmBtn.Style = (Style)FindResource("ButtonDanger");
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel (object sender, RoutedEventArgs e) => DialogResult = false;
}

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
    // Segoe MDL2 Assets code points — kept as numeric values so the
    // file stays pure-ASCII regardless of editor encoding.
    private const int GlyphInfo    = 0xE946; // Info
    private const int GlyphSuccess = 0xE930; // CompletedSolid
    private const int GlyphWarn    = 0xE7BA; // Warning
    private const int GlyphError   = 0xEA39; // ErrorBadge
    private const int GlyphDanger  = 0xE7BA; // Warning (matches "are you sure" tone)

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
        int    glyphCp;
        bool   dangerCta = false;

        switch (severity)
        {
            case ConfirmSeverity.Info:
                brush = (Brush)FindResource("InfoBrush");
                glyphCp = GlyphInfo;
                break;
            case ConfirmSeverity.Success:
                brush = (Brush)FindResource("OkBrush");
                glyphCp = GlyphSuccess;
                break;
            case ConfirmSeverity.Warning:
                brush = (Brush)FindResource("WarnBrush");
                glyphCp = GlyphWarn;
                break;
            case ConfirmSeverity.Error:
                brush = (Brush)FindResource("ErrBrush");
                glyphCp = GlyphError;
                dangerCta = true;
                break;
            case ConfirmSeverity.Danger:
                // Header neutral; only confirm button goes red — signals
                // "this is destructive but the user is choosing it" vs
                // "system-error report".
                brush = (Brush)FindResource("Accent");
                glyphCp = GlyphDanger;
                dangerCta = true;
                break;
            default:
                brush = (Brush)FindResource("Accent");
                glyphCp = GlyphInfo;
                break;
        }

        AccentStrip.Background = brush;
        HeaderIcon.Foreground  = brush;
        HeaderIcon.Text        = char.ConvertFromUtf32(glyphCp);

        if (dangerCta)
            ConfirmBtn.Style = (Style)FindResource("ButtonDanger");
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancel (object sender, RoutedEventArgs e) => DialogResult = false;
}

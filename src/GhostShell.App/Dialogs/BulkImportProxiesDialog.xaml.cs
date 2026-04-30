// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Bulk-import dialog. Live-previews the parser as the user types,
/// distinguishing valid lines, errors, and duplicates against the
/// existing library. Returns the chosen <see cref="ParsedProxy"/>
/// list via <see cref="Result"/>; the caller (DialogService /
/// ProxyViewModel) is in charge of converting them into Proxy
/// rows and persisting via IProxyService.BulkCreateAsync.
/// </summary>
public partial class BulkImportProxiesDialog : Window
{
    private readonly IReadOnlySet<string> _existingUrls;

    public IReadOnlyList<ParsedProxy>? Result { get; private set; }

    public ObservableCollection<PreviewRow> PreviewItems { get; } = new();

    public BulkImportProxiesDialog(IEnumerable<string> existingUrls)
    {
        // Set fields BEFORE InitializeComponent — XAML wires up the
        // SelectionChanged handler synchronously during parsing and
        // fires it the moment the first ComboBoxItem's IsSelected="True"
        // takes effect. If _existingUrls is still null at that point
        // (constructor body runs *after* InitializeComponent), the
        // handler dereferences null and crashes the dialog.
        _existingUrls = existingUrls.ToHashSet(StringComparer.OrdinalIgnoreCase);
        InitializeComponent();
        PreviewList.ItemsSource = PreviewItems;
        PasteField.Focus();
    }

    private void OnInputChanged(object? sender, EventArgs e)
    {
        // Defensive: handler can fire during XAML parsing before all
        // controls have x:Name'd themselves. Wait until the dialog
        // says it's loaded, otherwise we'd try to read a still-null
        // PasteField.
        if (!IsLoaded || PasteField is null || DefaultSchemeCombo is null)
            return;
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        var defaultScheme =
            (DefaultSchemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? "http";

        var parsed = ProxyParser.ParseBulk(PasteField.Text, defaultScheme);

        PreviewItems.Clear();
        var validNew = 0;
        var duplicates = 0;

        foreach (var p in parsed.Valid)
        {
            var dup = _existingUrls.Contains(p.Url ?? "");
            p.IsDuplicate = dup;
            if (dup) duplicates++;
            else validNew++;

            PreviewItems.Add(new PreviewRow
            {
                Status      = dup ? "≡" : "✓",
                StatusBrush = dup ? Brushes.Goldenrod : new SolidColorBrush(
                    Color.FromRgb(0x6E, 0xE7, 0xB7)),
                Headline    = p.Url ?? "",
                SubLine     = dup
                    ? $"already in library — will be skipped"
                    : $"format: {p.Format}",
            });
        }
        foreach (var err in parsed.Errors)
        {
            PreviewItems.Add(new PreviewRow
            {
                Status      = "✕",
                StatusBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
                Headline    = err.Raw,
                SubLine     = $"line {err.LineNumber}: {err.Error}",
            });
        }

        SummaryText.Text =
            $"{validNew} new · {duplicates} duplicate · {parsed.Errors.Count} errors " +
            $"({parsed.TotalNonBlankLines} non-blank lines total)";

        FooterStats.Text = parsed.TotalNonBlankLines == 0
            ? ""
            : $"Will import: {validNew}. Skipped: {duplicates + parsed.Errors.Count}.";

        ImportBtn.Content   = $"Import {validNew}";
        ImportBtn.IsEnabled = validNew > 0;

        // Tag final list for OnImport — only fresh ones.
        _stagedValid = parsed.Valid.Where(p => !p.IsDuplicate).ToList();
    }

    private List<ParsedProxy> _stagedValid = new();

    private void OnImport(object sender, RoutedEventArgs e)
    {
        Result = _stagedValid;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    public sealed class PreviewRow
    {
        public required string Status { get; init; }
        public required Brush StatusBrush { get; init; }
        public required string Headline { get; init; }
        public required string SubLine { get; init; }
    }
}

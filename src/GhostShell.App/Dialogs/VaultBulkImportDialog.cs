// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Win32;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 69 — bulk vault import. The user pastes CSV / picks a CSV file /
/// pastes a Google Sheets URL; we parse it client-side, let them map each
/// CSV column to a vault field (profile_name, name, seed_phrase,
/// password, …), pick a row range and a kind, and commit N items in one
/// pass via <see cref="IVaultService.CreateAsync"/>. Each row's
/// <c>profile_name</c> column auto-binds the new item to that profile —
/// the same mechanism used by the alias resolver, so the imported entries
/// are immediately reachable via <c>{{vault.SEED}}</c> et al.
///
/// CSV parser is intentionally hand-rolled (no CsvHelper dep): handles
/// quoted fields with embedded commas / quotes, CRLF, and a chooseable
/// delimiter (auto / comma / semicolon / tab / pipe). Google Sheets
/// "edit" URLs are rewritten to the export endpoint and fetched via the
/// already-injected <see cref="HttpClient"/>.
/// </summary>
public sealed class VaultBulkImportDialog : Window
{
    /// <summary>Number of items successfully created — populated when
    /// the dialog closes with <c>DialogResult = true</c> so the caller
    /// can show a "imported N" toast and refresh the list.</summary>
    public int CreatedCount { get; private set; }

    private readonly IVaultService _vault;
    private readonly IReadOnlyList<Profile> _profiles;

    // Source mode
    private readonly RadioButton _srcPaste;
    private readonly RadioButton _srcFile;
    private readonly RadioButton _srcSheets;

    // Inputs
    private readonly TextBox _pasteField;
    private readonly TextBox _filePathField;
    private readonly TextBox _sheetsUrlField;
    private readonly Button  _browseBtn;
    private readonly Button  _fetchBtn;

    // Options
    private readonly ComboBox _delimiterCombo;
    private readonly CheckBox _hasHeader;
    private readonly ComboBox _kindCombo;
    private readonly TextBox  _fromRow;
    private readonly TextBox  _toRow;
    private readonly CheckBox _stopOnError;
    private readonly CheckBox _skipEmptyProfile;

    // Mapping
    private readonly StackPanel _mappingPanel;
    private readonly StackPanel _previewPanel;
    private readonly TextBlock  _statusLine;
    private readonly TextBlock  _errorLabel;

    // Footer
    private readonly Button _importBtn;

    // Runtime state
    private List<List<string>> _rows = new();   // parsed rows, including header if present
    private List<string> _headerRow = new();    // original header values (lowercased) — for auto-map
    private List<ComboBox> _mappingCombos = new();
    private List<ParsedRow> _stagedRows = new();
    private bool _refreshing;

    // Field choices the user picks from per column. The base list is
    // profile_name + plain metadata; the secret fields are appended
    // based on the chosen Kind so the dropdown only shows fields that
    // actually mean something for that kind.
    private static readonly string[] BaseChoices = new[]
    {
        "(skip)",
        "profile_name",   // binds vault item to profile by name
        "name",           // human label
        "identifier",
        "service",
        "notes",
        "tags",           // comma-separated → JSON array
        "status",
    };

    public VaultBulkImportDialog(IVaultService vault, IReadOnlyList<Profile> profiles)
    {
        _vault = vault;
        _profiles = profiles;

        Title = "Bulk import vault items";
        Width = 980;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // body
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // footer

        // ── Header ──
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var title = new TextBlock
        {
            Text = "📥  Bulk import vault items",
            FontSize = 16, FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        head.Children.Add(title);
        var sub = new TextBlock
        {
            Text = "Paste CSV, pick a file, or fetch a Google Sheet. Map each column to a vault field, " +
                   "pick a kind + row range, and commit. Rows whose 'profile_name' column matches an " +
                   "existing profile are auto-bound, so the new entries resolve through {{vault.SEED}} etc.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        head.Children.Add(sub);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // ── Body (scrollable) ──
        var body = new ScrollViewer
        {
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        var stack = new StackPanel();
        body.Content = stack;
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        // 1. Source picker
        stack.Children.Add(MakeSection("1.  Source"));
        var srcPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _srcPaste  = new RadioButton { Content = "Paste CSV",         IsChecked = true, Margin = new Thickness(0, 0, 16, 0), GroupName = "src" };
        _srcFile   = new RadioButton { Content = "CSV file",          Margin = new Thickness(0, 0, 16, 0), GroupName = "src" };
        _srcSheets = new RadioButton { Content = "Google Sheets URL", GroupName = "src" };
        srcPanel.Children.Add(_srcPaste);
        srcPanel.Children.Add(_srcFile);
        srcPanel.Children.Add(_srcSheets);
        stack.Children.Add(srcPanel);

        // Paste pane
        _pasteField = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = false,
            FontFamily = (FontFamily?)Application.Current.Resources["FontMono"]
                         ?? new FontFamily("Consolas"),
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            MinHeight = 120,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };
        stack.Children.Add(_pasteField);

        // File pane
        var fileRow = new Grid { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _filePathField = new TextBox { Padding = new Thickness(8, 6, 8, 6) };
        Grid.SetColumn(_filePathField, 0);
        _browseBtn = new Button { Content = "Browse…", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(_browseBtn, 1);
        fileRow.Children.Add(_filePathField);
        fileRow.Children.Add(_browseBtn);
        stack.Children.Add(fileRow);

        // Sheets pane
        var sheetsRow = new Grid { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        sheetsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sheetsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _sheetsUrlField = new TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            ToolTip = "Paste the share URL — sheet must be set to 'Anyone with the link can view'.\n" +
                      "Example: https://docs.google.com/spreadsheets/d/<id>/edit#gid=0",
        };
        Grid.SetColumn(_sheetsUrlField, 0);
        _fetchBtn = new Button { Content = "Fetch", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(_fetchBtn, 1);
        sheetsRow.Children.Add(_sheetsUrlField);
        sheetsRow.Children.Add(_fetchBtn);
        stack.Children.Add(sheetsRow);

        _srcPaste.Checked  += (_, _) => { _pasteField.Visibility = Visibility.Visible;  fileRow.Visibility = Visibility.Collapsed; sheetsRow.Visibility = Visibility.Collapsed; RefreshAll(); };
        _srcFile.Checked   += (_, _) => { _pasteField.Visibility = Visibility.Collapsed; fileRow.Visibility = Visibility.Visible;   sheetsRow.Visibility = Visibility.Collapsed; RefreshAll(); };
        _srcSheets.Checked += (_, _) => { _pasteField.Visibility = Visibility.Collapsed; fileRow.Visibility = Visibility.Collapsed; sheetsRow.Visibility = Visibility.Visible;  RefreshAll(); };
        _pasteField.TextChanged += (_, _) => RefreshAll();
        _browseBtn.Click += async (_, _) => await OnBrowseAsync();
        _fetchBtn.Click  += async (_, _) => await OnFetchSheetAsync();

        // 2. Parse options
        stack.Children.Add(MakeSection("2.  Parse options"));
        var optsGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        for (var i = 0; i < 4; i++)
            optsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        optsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddOptLabel(optsGrid, 0, "Delimiter");
        _delimiterCombo = new ComboBox
        {
            Width = 110,
            Margin = new Thickness(0, 0, 16, 0),
            Items = { "Auto-detect", "Comma (,)", "Semicolon (;)", "Tab", "Pipe (|)" },
            SelectedIndex = 0,
        };
        _delimiterCombo.SelectionChanged += (_, _) => RefreshAll();
        Grid.SetColumn(_delimiterCombo, 1);
        optsGrid.Children.Add(_delimiterCombo);

        _hasHeader = new CheckBox
        {
            Content = "First row is header",
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        _hasHeader.Checked   += (_, _) => RefreshAll();
        _hasHeader.Unchecked += (_, _) => RefreshAll();
        Grid.SetColumn(_hasHeader, 2);
        optsGrid.Children.Add(_hasHeader);

        _stopOnError = new CheckBox
        {
            Content = "Stop on first error",
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            ToolTip = "If unchecked, rows that fail (e.g. duplicate name) are skipped and the import continues.",
        };
        Grid.SetColumn(_stopOnError, 3);
        optsGrid.Children.Add(_stopOnError);

        _skipEmptyProfile = new CheckBox
        {
            Content = "Skip rows whose profile_name doesn't match any profile",
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Useful when the sheet has rows for profiles that haven't been created yet. " +
                      "Off = imports unbound items (no profile binding).",
        };
        Grid.SetColumn(_skipEmptyProfile, 4);
        optsGrid.Children.Add(_skipEmptyProfile);

        stack.Children.Add(optsGrid);

        // 3. Item shape
        stack.Children.Add(MakeSection("3.  Item shape"));
        var shapeGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddOptLabel(shapeGrid, 0, "Kind");
        _kindCombo = new ComboBox
        {
            Width = 200,
            Margin = new Thickness(0, 0, 16, 0),
            ItemsSource = VaultKinds.Catalog,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Id",
            SelectedValue = "crypto_wallet",
        };
        _kindCombo.SelectionChanged += (_, _) => RebuildMappingDropdowns(refreshPreviewToo: true);
        Grid.SetColumn(_kindCombo, 1);
        shapeGrid.Children.Add(_kindCombo);

        AddOptLabel(shapeGrid, 2, "Row range");
        _fromRow = new TextBox { Text = "1", Width = 60, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 6, 0) };
        _fromRow.TextChanged += (_, _) => UpdatePreview();
        Grid.SetColumn(_fromRow, 3);
        shapeGrid.Children.Add(_fromRow);

        var toLabel = new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        toLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetColumn(toLabel, 4);
        shapeGrid.Children.Add(toLabel);

        _toRow = new TextBox { Text = "9999", Width = 70, Padding = new Thickness(6, 4, 6, 4) };
        _toRow.TextChanged += (_, _) => UpdatePreview();
        Grid.SetColumn(_toRow, 5);
        shapeGrid.Children.Add(_toRow);

        stack.Children.Add(shapeGrid);

        // 4. Column mapping
        stack.Children.Add(MakeSection("4.  Column mapping"));
        _mappingPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var mappingHelp = new TextBlock
        {
            Text = "Pick what each column means. Set a column to '(skip)' to ignore it.",
            FontSize = 10,
            Margin = new Thickness(0, 0, 0, 6),
        };
        mappingHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _mappingPanel.Children.Add(mappingHelp);
        stack.Children.Add(_mappingPanel);

        // 5. Preview
        stack.Children.Add(MakeSection("5.  Preview (first 5 rows after mapping)"));
        var previewBorder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 12),
        };
        previewBorder.SetResourceReference(BorderBrushProperty, "Border");
        previewBorder.SetResourceReference(BackgroundProperty, "BgRaised");
        _previewPanel = new StackPanel();
        previewBorder.Child = _previewPanel;
        stack.Children.Add(previewBorder);

        // Status line
        _statusLine = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        };
        _statusLine.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(_statusLine);

        _errorLabel = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };
        _errorLabel.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        stack.Children.Add(_errorLabel);

        // ── Footer ──
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        _importBtn = new Button { Content = "Import 0", MinWidth = 130, IsDefault = true, IsEnabled = false };
        _importBtn.SetResourceReference(StyleProperty, "ButtonPrimary");
        _importBtn.Click += async (_, _) => await OnImportAsync();
        btns.Children.Add(cancel);
        btns.Children.Add(_importBtn);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;

        // First-time render — empty state.
        RebuildMappingDropdowns(refreshPreviewToo: false);
        _pasteField.Focus();

        // Wipe paste field on close — it might contain seed phrases.
        Closed += (_, _) =>
        {
            try { _pasteField.Clear(); _filePathField.Clear(); _sheetsUrlField.Clear(); } catch { /* ignore */ }
        };
    }

    // ─── Source loaders ──────────────────────────────────────────

    private async Task OnBrowseAsync()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "CSV / TSV (*.csv;*.tsv;*.txt)|*.csv;*.tsv;*.txt|All files (*.*)|*.*",
            Title  = "Pick a CSV file",
        };
        if (dlg.ShowDialog(this) != true) return;
        _filePathField.Text = dlg.FileName;
        try
        {
            // Read with auto-BOM-detection. Don't lock the file (FileShare.Read)
            // so the user can keep the source open in Excel.
            using var fs = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            _pasteField.Text = await sr.ReadToEndAsync();
            // Surface paste field for parsing — user can also tweak.
            RefreshAll();
        }
        catch (Exception ex)
        {
            ShowError("Couldn't read file: " + ex.Message);
        }
    }

    private async Task OnFetchSheetAsync()
    {
        var url = (_sheetsUrlField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(url)) { ShowError("Paste a Google Sheets URL first."); return; }

        var exportUrl = ToSheetsExportUrl(url);
        if (exportUrl is null)
        {
            ShowError("Doesn't look like a Google Sheets URL. Expected " +
                      "https://docs.google.com/spreadsheets/d/<id>/...");
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        _fetchBtn.IsEnabled = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // GS export endpoint returns text/csv with no auth required for
            // anyone-with-link sheets. If the sheet is private, GS responds
            // with the sign-in HTML — we surface that as a friendly error.
            var resp = await http.GetAsync(exportUrl);
            if ((int)resp.StatusCode >= 400)
            {
                ShowError($"Sheet fetch failed: HTTP {(int)resp.StatusCode}. " +
                          "Make sure the sheet is shared as 'Anyone with the link can view'.");
                return;
            }
            var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
            var body = await resp.Content.ReadAsStringAsync();
            if (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                ShowError("Google returned a sign-in page — the sheet isn't public. " +
                          "Set sharing to 'Anyone with the link can view' and retry.");
                return;
            }
            _pasteField.Text = body;
            RefreshAll();
        }
        catch (Exception ex)
        {
            ShowError("Sheet fetch failed: " + ex.Message);
        }
        finally
        {
            _fetchBtn.IsEnabled = true;
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// Convert a Google Sheets share URL into the CSV-export endpoint:
    /// .../d/&lt;id&gt;/edit?...#gid=N → .../d/&lt;id&gt;/export?format=csv&amp;gid=N
    /// Returns null if the URL doesn't look like a sheets URL.
    /// </summary>
    internal static string? ToSheetsExportUrl(string url)
    {
        var m = Regex.Match(url,
            @"docs\.google\.com/spreadsheets/d/(?<id>[A-Za-z0-9_\-]+)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var id = m.Groups["id"].Value;
        // Try to preserve the gid (specific tab) — both ?gid= and #gid= forms.
        var gid = "0";
        var gm = Regex.Match(url, @"[?&#]gid=(?<g>\d+)", RegexOptions.IgnoreCase);
        if (gm.Success) gid = gm.Groups["g"].Value;
        return $"https://docs.google.com/spreadsheets/d/{id}/export?format=csv&gid={gid}";
    }

    // ─── Parsing ─────────────────────────────────────────────────

    private void RefreshAll()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            ClearError();
            ParseSource();
            RebuildMappingDropdowns(refreshPreviewToo: true);
        }
        finally { _refreshing = false; }
    }

    private void ParseSource()
    {
        var raw = _pasteField.Text ?? "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            _rows = new();
            _headerRow = new();
            return;
        }

        var delim = ResolveDelimiter(raw);
        _rows = ParseCsv(raw, delim);

        // Strip trailing fully-empty rows — Google export often pads.
        while (_rows.Count > 0 && _rows[^1].All(string.IsNullOrWhiteSpace))
            _rows.RemoveAt(_rows.Count - 1);

        _headerRow = (_rows.Count > 0 && _hasHeader.IsChecked == true)
            ? _rows[0].Select(s => (s ?? "").Trim().ToLowerInvariant()).ToList()
            : new List<string>();
    }

    private char ResolveDelimiter(string sample)
    {
        var sel = _delimiterCombo.SelectedIndex;
        return sel switch
        {
            1 => ',',
            2 => ';',
            3 => '\t',
            4 => '|',
            _ => AutoDetectDelimiter(sample),
        };
    }

    /// <summary>
    /// Pick the delimiter whose count is most consistent across the
    /// first few lines. Defaults to comma if everything ties.
    /// </summary>
    internal static char AutoDetectDelimiter(string sample)
    {
        var lines = sample.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                          .Take(5).ToArray();
        if (lines.Length == 0) return ',';
        var candidates = new[] { ',', '\t', ';', '|' };
        var bestChar = ',';
        var bestScore = -1.0;
        foreach (var c in candidates)
        {
            var counts = lines.Select(l => CountOutsideQuotes(l, c)).ToArray();
            if (counts[0] == 0) continue;
            // Score: first-line count, penalised by inconsistency.
            var avg = counts.Average();
            var dev = counts.Select(x => Math.Abs(x - avg)).Sum();
            var score = avg - dev * 0.5;
            if (score > bestScore) { bestScore = score; bestChar = c; }
        }
        return bestChar;
    }

    private static int CountOutsideQuotes(string line, char target)
    {
        var inQ = false;
        var n = 0;
        foreach (var ch in line)
        {
            if (ch == '"') inQ = !inQ;
            else if (!inQ && ch == target) n++;
        }
        return n;
    }

    /// <summary>
    /// RFC-4180-ish CSV parser. Handles quoted fields with embedded
    /// delimiters / quotes / newlines, plus CRLF / LF line endings.
    /// </summary>
    internal static List<List<string>> ParseCsv(string text, char delim)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else inQuotes = false;
                }
                else field.Append(c);
                continue;
            }
            if (c == '"') { inQuotes = true; continue; }
            if (c == delim)
            {
                row.Add(field.ToString());
                field.Clear();
                continue;
            }
            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
                continue;
            }
            if (c == '\n')
            {
                row.Add(field.ToString()); field.Clear();
                rows.Add(row); row = new List<string>();
                continue;
            }
            field.Append(c);
        }
        // Trailing line not terminated by newline.
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }

    // ─── Mapping UI ──────────────────────────────────────────────

    private string[] BuildChoiceList()
    {
        var kind = (_kindCombo?.SelectedValue as string) ?? "crypto_wallet";
        var spec = VaultKinds.Get(kind);
        var fields = spec?.Fields ?? Array.Empty<string>();
        var list = new List<string>(BaseChoices);
        foreach (var f in fields)
            if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        return list.ToArray();
    }

    private void RebuildMappingDropdowns(bool refreshPreviewToo)
    {
        // Drop existing combos (keep the help label at index 0).
        var keep = _mappingPanel.Children.Count > 0 ? _mappingPanel.Children[0] : null;
        _mappingPanel.Children.Clear();
        if (keep is not null) _mappingPanel.Children.Add(keep);
        _mappingCombos.Clear();

        if (_rows.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "(paste / load data above to map columns)",
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 0),
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            _mappingPanel.Children.Add(empty);
            UpdatePreview();
            return;
        }

        var choices = BuildChoiceList();
        var colCount = _rows.Max(r => r.Count);

        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Header row
        AddCell(grid, 0, 0, "#", true);
        AddCell(grid, 0, 1, "Header", true);
        AddCell(grid, 0, 2, "Map to", true);
        AddCell(grid, 0, 3, "Sample value", true);

        for (var col = 0; col < colCount; col++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var rowIdx = col + 1;

            AddCell(grid, rowIdx, 0, (col + 1).ToString(), false);

            var headerText = (_hasHeader.IsChecked == true && col < _headerRow.Count)
                ? _rows[0][col]
                : $"Column {col + 1}";
            AddCell(grid, rowIdx, 1, headerText, false);

            var combo = new ComboBox
            {
                ItemsSource = choices,
                Margin = new Thickness(0, 2, 8, 2),
                Padding = new Thickness(4, 2, 4, 2),
            };
            combo.SelectedItem = GuessFieldForColumn(col, choices);
            combo.SelectionChanged += (_, _) => UpdatePreview();
            Grid.SetRow(combo, rowIdx); Grid.SetColumn(combo, 2);
            grid.Children.Add(combo);
            _mappingCombos.Add(combo);

            // Sample value: pick from row #1 (after header).
            var dataStart = (_hasHeader.IsChecked == true) ? 1 : 0;
            var sample = (_rows.Count > dataStart && col < _rows[dataStart].Count)
                ? _rows[dataStart][col]
                : "";
            AddCell(grid, rowIdx, 3, TruncateForDisplay(sample), false);
        }
        _mappingPanel.Children.Add(grid);

        if (refreshPreviewToo) UpdatePreview();
    }

    private string GuessFieldForColumn(int col, string[] choices)
    {
        var headerHint = (_hasHeader.IsChecked == true && col < _headerRow.Count)
            ? _headerRow[col] : "";
        if (string.IsNullOrEmpty(headerHint)) return choices[0]; // (skip)

        // Try direct match first.
        foreach (var ch in choices)
            if (string.Equals(ch, headerHint, StringComparison.OrdinalIgnoreCase))
                return ch;

        // Fuzzy aliases.
        var aliases = new (string col, string field)[]
        {
            ("profile",        "profile_name"),
            ("profilename",    "profile_name"),
            ("profile_name",   "profile_name"),
            ("login",          "username"),
            ("email",          "username"),
            ("user",           "username"),
            ("password",       "password"),
            ("pwd",            "password"),
            ("pass",           "password"),
            ("walletpassword", "wallet_password"),
            ("wallet_password","wallet_password"),
            ("seed",           "seed_phrase"),
            ("seedphrase",     "seed_phrase"),
            ("seed_phrase",    "seed_phrase"),
            ("mnemonic",       "seed_phrase"),
            ("phrase",         "seed_phrase"),
            ("privkey",        "private_key"),
            ("private_key",    "private_key"),
            ("privatekey",     "private_key"),
            ("address",        "address"),
            ("addr",           "address"),
            ("totp",           "totp_secret"),
            ("2fa",            "totp_secret"),
            ("derivation",     "derivation_path"),
            ("path",           "derivation_path"),
            ("name",           "name"),
            ("label",          "name"),
            ("notes",          "notes"),
            ("note",           "notes"),
            ("comment",        "notes"),
            ("identifier",     "identifier"),
            ("service",        "service"),
            ("tags",           "tags"),
            ("status",         "status"),
        };

        var norm = Regex.Replace(headerHint, @"\s|_|-", "").ToLowerInvariant();
        foreach (var (alias, field) in aliases)
        {
            var aliasNorm = alias.Replace("_", "").Replace("-", "").ToLowerInvariant();
            if (norm == aliasNorm && choices.Contains(field, StringComparer.OrdinalIgnoreCase))
                return field;
        }
        // Substring fallback.
        foreach (var (alias, field) in aliases)
        {
            if (norm.Contains(alias.Replace("_", "").Replace("-", "")) &&
                choices.Contains(field, StringComparer.OrdinalIgnoreCase))
                return field;
        }
        return choices[0]; // (skip)
    }

    // ─── Preview + staging ───────────────────────────────────────

    private void UpdatePreview()
    {
        _previewPanel.Children.Clear();
        _stagedRows.Clear();

        var dataStart = (_hasHeader.IsChecked == true) ? 1 : 0;
        var fromN = ParseIntOr(_fromRow.Text, 1);
        var toN   = ParseIntOr(_toRow.Text, 99999);
        if (fromN < 1) fromN = 1;
        if (toN < fromN) toN = fromN;

        var kind = (_kindCombo?.SelectedValue as string) ?? "crypto_wallet";
        var profileNames = _profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var errors = 0;
        var warnings = 0;
        var dataIdx = 0;
        for (var i = dataStart; i < _rows.Count; i++)
        {
            dataIdx++;
            if (dataIdx < fromN) continue;
            if (dataIdx > toN) break;

            var raw = _rows[i];
            // Skip fully-empty rows silently.
            if (raw.All(string.IsNullOrWhiteSpace)) continue;

            var staged = MapRow(raw, kind);
            staged.OriginalRowNumber = i + 1; // 1-indexed including header

            // Validation.
            string? err = null;
            if (string.IsNullOrWhiteSpace(staged.Item.Name))
                err = "no 'name' column mapped";

            if (_skipEmptyProfile.IsChecked == true &&
                !string.IsNullOrEmpty(staged.Item.ProfileName) &&
                !profileNames.Contains(staged.Item.ProfileName))
                err = $"profile '{staged.Item.ProfileName}' doesn't exist";

            if (err is null && _skipEmptyProfile.IsChecked == true &&
                string.IsNullOrEmpty(staged.Item.ProfileName))
                err = "row has no profile_name";

            if (err is not null)
            {
                staged.Error = err;
                errors++;
            }
            else if (!string.IsNullOrEmpty(staged.Item.ProfileName) &&
                     !profileNames.Contains(staged.Item.ProfileName))
            {
                staged.Warning = $"profile '{staged.Item.ProfileName}' not found — will save as unbound";
                warnings++;
            }

            _stagedRows.Add(staged);
        }

        // Build preview cards (first 5).
        foreach (var s in _stagedRows.Take(5))
            _previewPanel.Children.Add(BuildPreviewRow(s));
        if (_stagedRows.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = _rows.Count == 0
                    ? "(no data parsed yet)"
                    : "(no rows fall in the selected range)",
                FontStyle = FontStyles.Italic,
            };
            empty.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            _previewPanel.Children.Add(empty);
        }
        else if (_stagedRows.Count > 5)
        {
            var more = new TextBlock
            {
                Text = $"… and {_stagedRows.Count - 5} more row(s)",
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 4, 0, 0),
            };
            more.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            _previewPanel.Children.Add(more);
        }

        var ready = _stagedRows.Count(r => r.Error is null);
        _statusLine.Text =
            $"{ready} ready · {errors} skipped · {warnings} warnings  ({_rows.Count - dataStart} data row(s) parsed)";
        _importBtn.Content   = $"Import {ready}";
        _importBtn.IsEnabled = ready > 0;
    }

    private FrameworkElement BuildPreviewRow(ParsedRow r)
    {
        var border = new Border
        {
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 6, 0, 6),
        };
        border.SetResourceReference(BorderBrushProperty, "Border");

        var stack = new StackPanel();
        var title = new TextBlock
        {
            FontFamily = (FontFamily?)Application.Current.Resources["FontMono"]
                         ?? new FontFamily("Consolas"),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        var label = string.IsNullOrEmpty(r.Item.Name) ? "(unnamed)" : r.Item.Name;
        var prof  = string.IsNullOrEmpty(r.Item.ProfileName) ? "(unbound)" : r.Item.ProfileName;
        var icon  = r.Error is not null ? "✕" : (r.Warning is not null ? "⚠" : "✓");
        title.Text = $"{icon}  row {r.OriginalRowNumber}:  {label}   →   profile: {prof}";
        if (r.Error is not null)
            title.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
        else if (r.Warning is not null)
            title.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        stack.Children.Add(title);

        // Sub-line: redacted secret summary.
        var secretSummary = string.Join(" · ",
            r.Secrets.Where(kv => !string.IsNullOrEmpty(kv.Value))
                     .Select(kv => $"{kv.Key}={Redact(kv.Value)}"));
        if (string.IsNullOrEmpty(secretSummary)) secretSummary = "(no secrets mapped)";

        var sub = new TextBlock
        {
            Text = secretSummary,
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(sub);

        if (r.Error is not null)
        {
            var e = new TextBlock { Text = "✗ " + r.Error, FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
            e.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            stack.Children.Add(e);
        }
        else if (r.Warning is not null)
        {
            var w = new TextBlock { Text = "⚠ " + r.Warning, FontSize = 10, Margin = new Thickness(0, 2, 0, 0) };
            w.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
            stack.Children.Add(w);
        }

        border.Child = stack;
        return border;
    }

    private ParsedRow MapRow(List<string> raw, string kind)
    {
        var item = new VaultItem { Name = "", Kind = kind };
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        var notes   = (string?)null;
        var status  = (string?)null;
        var tags    = (string?)null;
        string? name = null;
        string? identifier = null;
        string? service = null;
        string? profile = null;

        for (var col = 0; col < _mappingCombos.Count && col < raw.Count; col++)
        {
            var target = (_mappingCombos[col].SelectedItem as string) ?? "(skip)";
            if (target == "(skip)") continue;
            var val = (raw[col] ?? "").Trim();
            if (string.IsNullOrEmpty(val)) continue;

            switch (target)
            {
                case "profile_name": profile    = val; break;
                case "name":         name       = val; break;
                case "identifier":   identifier = val; break;
                case "service":      service    = val; break;
                case "notes":        notes      = val; break;
                case "tags":         tags       = val; break;
                case "status":       status     = val; break;
                default:
                    // Anything else lands in secrets — caller will encrypt.
                    secrets[target] = val;
                    break;
            }
        }

        // Auto-fill name if not mapped: prefer "<service> · <profile>".
        if (string.IsNullOrEmpty(name))
        {
            if (!string.IsNullOrEmpty(profile))
                name = $"{KindLabel(kind)} · {profile}";
            else if (!string.IsNullOrEmpty(identifier))
                name = identifier;
        }

        item = item with
        {
            Name        = name ?? "",
            Kind        = kind,
            Identifier  = string.IsNullOrEmpty(identifier) ? null : identifier,
            Service     = string.IsNullOrEmpty(service) ? null : service,
            ProfileName = string.IsNullOrEmpty(profile) ? null : profile,
            Status      = !string.IsNullOrEmpty(status) && VaultKinds.IsValidStatus(status) ? status : "active",
            Notes       = notes,
            TagsJson    = string.IsNullOrEmpty(tags) ? null
                           : System.Text.Json.JsonSerializer.Serialize(
                                tags.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                | StringSplitOptions.TrimEntries)),
        };
        return new ParsedRow { Item = item, Secrets = secrets };
    }

    private static string KindLabel(string kind) => VaultKinds.Get(kind)?.Label ?? kind;

    // ─── Commit ──────────────────────────────────────────────────

    private async Task OnImportAsync()
    {
        if (!_vault.IsUnlocked)
        {
            ShowError("Vault is locked. Unlock first then retry.");
            return;
        }

        var ready = _stagedRows.Where(r => r.Error is null).ToList();
        if (ready.Count == 0) { ShowError("Nothing to import."); return; }

        _importBtn.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        var created = 0;
        var failed  = new List<(int row, string err)>();
        try
        {
            foreach (var r in ready)
            {
                try
                {
                    await _vault.CreateAsync(r.Item, r.Secrets);
                    created++;
                }
                catch (Exception ex)
                {
                    failed.Add((r.OriginalRowNumber, ex.Message));
                    if (_stopOnError.IsChecked == true) break;
                }
            }
            CreatedCount = created;
            if (failed.Count > 0)
            {
                var msg = $"Imported {created} of {ready.Count}. Errors:\n" +
                          string.Join("\n", failed.Take(10)
                              .Select(f => $"  · row {f.row}: {f.err}"));
                if (failed.Count > 10) msg += $"\n  · …and {failed.Count - 10} more";
                MessageBox.Show(this, msg, "Bulk import — partial",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowError("Import failed: " + ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _importBtn.IsEnabled = true;
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────

    private static int ParseIntOr(string? s, int fallback)
        => int.TryParse(s, out var v) ? v : fallback;

    private static string TruncateForDisplay(string s)
    {
        s = (s ?? "").Replace("\n", " ").Replace("\r", " ").Trim();
        return s.Length > 48 ? s[..48] + "…" : s;
    }

    /// <summary>
    /// Mask cleartext secrets in the preview so a screen-share doesn't
    /// leak seeds / passwords. Shows the length so the user can sanity-
    /// check that they mapped the right column.
    /// </summary>
    private static string Redact(string v)
    {
        v = v.Trim();
        if (v.Length <= 4) return new string('•', v.Length);
        // Multi-word fields (seeds): show word count.
        var wordCount = v.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount >= 6) return $"({wordCount} words, {v.Length} chars)";
        return $"{v[..2]}{new string('•', Math.Min(v.Length - 4, 8))}{v[^2..]}";
    }

    private void ShowError(string msg)
    {
        _errorLabel.Text = "✗  " + msg;
        _errorLabel.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        _errorLabel.Text = "";
        _errorLabel.Visibility = Visibility.Collapsed;
    }

    private static TextBlock MakeSection(string text)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 8, 0, 6),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        return t;
    }

    private static void AddOptLabel(Grid g, int col, string text)
    {
        var t = new TextBlock
        {
            Text = text + ":",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetColumn(t, col);
        g.Children.Add(t);
    }

    private static void AddCell(Grid g, int row, int col, string text, bool header)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 8, 4),
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, header ? "TextMuted" : "Text");
        if (g.RowDefinitions.Count <= row)
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(t, row);
        Grid.SetColumn(t, col);
        g.Children.Add(t);
    }

    /// <summary>One staged row ready (or not) for commit.</summary>
    private sealed class ParsedRow
    {
        public required VaultItem Item { get; set; }
        public required Dictionary<string, string> Secrets { get; init; }
        public string? Error { get; set; }
        public string? Warning { get; set; }
        public int OriginalRowNumber { get; set; }
    }
}

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

    /// <summary>Phase 70 — number of profiles auto-created during
    /// commit (only when 'Auto-create profile' was on). Surfaced to
    /// the caller's toast so the user knows new profile rows
    /// appeared on the Profiles page too.</summary>
    public int CreatedProfilesCount { get; private set; }

    private readonly IVaultService _vault;
    private IReadOnlyList<Profile> _profiles;
    private readonly IProfileService? _profileService;

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

    // Phase 70 — auto-create profile on commit. When the user is
    // bulk-importing 500 social-media accounts and DOESN'T have those
    // profiles created yet, the old "skip if missing" behaviour was
    // useless — we now auto-create matching Profile rows so the
    // imported creds are immediately bound + reachable through
    // {{vault.USERNAME}} et al.
    //
    // Smart binding rules:
    //  • profile_name column mapped + non-empty value → ensure profile
    //    exists (create if missing) + bind cred to it.
    //  • profile_name not mapped (or empty) AND _defaultProfile set →
    //    every row binds to that one default profile (created once).
    //  • neither → cred is created unbound (legacy behaviour).
    private readonly CheckBox _autoCreateProfile;
    private readonly TextBox  _defaultProfileName;

    /// <summary>
    /// Phase 71d — dedicated "Name source" picker. The user selects
    /// which CSV column should provide the imported item's
    /// <see cref="VaultItem.Name"/>:
    ///   • <c>SelectedIndex == 0</c> → auto-generate
    ///     ("Universal row N · timestamp"), the current default.
    ///   • <c>SelectedIndex &gt; 0</c> → use raw value of that column.
    ///
    /// Items are rebuilt from header / "Column N" strings whenever
    /// <see cref="ParseSource"/> rebuilds the table; the previous
    /// selection (by header text, not index) is preserved when
    /// possible. Independent of the per-row "Map to" combos —
    /// using this picker doesn't consume one of the column slots.
    /// </summary>
    private readonly ComboBox _nameSourceCombo;

    /// <summary>
    /// Phase 70c audit fix — when on, rows whose synthesised
    /// (Name, Kind, ProfileName) tuple already exists in the vault
    /// are skipped at commit time so re-importing the same CSV
    /// doesn't create silent duplicates. Off by default to preserve
    /// the legacy "always insert" behaviour for users who genuinely
    /// want to add a second copy (e.g. importing a refreshed dump
    /// of the same accounts with new TOTP/passwords).
    /// </summary>
    private readonly CheckBox _skipExisting;

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

    /// <summary>
    /// Phase 70b — user-defined custom field keys added during THIS
    /// dialog session ONLY. The "+ Add custom field…" sentinel in the
    /// per-column dropdown opens <see cref="CustomFieldDialog"/>; the
    /// entered key is normalised (snake_case, lowercased) and added
    /// here so it appears in every column's dropdown for the rest of
    /// the open session.
    ///
    /// Phase 71d — explicitly NOT persisted across imports. Each
    /// <c>new VaultBulkImportDialog(...)</c> gets a fresh empty list,
    /// so opening Bulk Import a second time only shows the canonical
    /// default fields + the "+ Add custom field…" sentinel — your
    /// previous import's discord_token, twitter_ct0, etc. won't leak
    /// into the next import's dropdown. One vault record can have one
    /// set of fields and another record an entirely different set.
    /// </summary>
    private readonly List<string> _customFields = new();

    /// <summary>
    /// Phase 71 — per-custom-field metadata captured during the
    /// "+ Add custom field…" flow (encrypted? is_totp?). Built-in
    /// keys (username/password/totp_secret/email_password/…) default
    /// to encrypted; explicit plaintext keys (email/name/notes/…)
    /// default to plaintext. User-added keys read from this map.
    /// </summary>
    private readonly Dictionary<string, VaultFieldMeta> _customFieldMeta =
        new(StringComparer.Ordinal);

    private const string CustomFieldSentinel = "+ Add custom field…";

    /// <summary>
    /// Phase 70c audit fix — short timestamp suffix used to disambiguate
    /// auto-synthesised names ("Account row 5 · 0506-1234") so the same
    /// CSV imported twice doesn't produce identical Name+Kind pairs.
    /// Stamped once when the dialog opens; survives RebuildMappingDropdowns
    /// because we generate the same value across re-renders within one
    /// dialog session.
    /// </summary>
    private readonly string _importStamp = DateTime.Now.ToString("MMdd-HHmm");

    // Field choices the user picks from per column. The base list is
    // profile_name + plain metadata; the secret fields are appended
    // based on the chosen Kind so the dropdown only shows fields that
    // actually mean something for that kind.
    // Phase 71d — pruned to only the canonical universal-vault fields
    // the user explicitly asked to keep: name, email, email_password,
    // profile_name, status, tags, notes. Everything else (username,
    // password, totp_secret, twitter_ct0, discord_token, …) goes
    // through the "+ Add custom field…" sentinel so users can choose
    // the encrypted/TOTP flags per field.
    //
    // email_password is appended automatically because the "universal"
    // kind's VaultKinds.Catalog spec lists it under Fields, and
    // BuildChoiceList unions BaseChoices with the kind's Fields.
    private static readonly string[] BaseChoices = new[]
    {
        "(skip)",
        "profile_name",   // binds vault item to profile by name
        "name",           // human-readable label
        "email",          // searchable plaintext email column
        "notes",
        "tags",           // comma-separated → JSON array
        "status",
    };

    public VaultBulkImportDialog(
        IVaultService vault,
        IReadOnlyList<Profile> profiles,
        IProfileService? profileService = null)
    {
        _vault = vault;
        _profiles = profiles;
        _profileService = profileService;

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
                   "pick a row range, and commit. Rows whose 'profile_name' column matches an " +
                   "existing profile are auto-bound, so the new entries resolve through {{vault.<field>}} etc. " +
                   "All imported items are saved as universal vault records — add any custom fields via " +
                   "the \"+ Add custom field…\" entry in any column dropdown.",
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
                      "Off = imports unbound items (no profile binding). Disabled when " +
                      "'Auto-create profile' is on.",
        };
        Grid.SetColumn(_skipEmptyProfile, 4);
        optsGrid.Children.Add(_skipEmptyProfile);

        stack.Children.Add(optsGrid);

        // Phase 70 — Auto-create profile row. Lives below the options
        // grid as its own row because it needs a textbox alongside it.
        var profCreateRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        profCreateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        profCreateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        profCreateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        profCreateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _autoCreateProfile = new CheckBox
        {
            Content = "Auto-create profile if missing",
            IsChecked = _profileService is not null, // default-on when service is wired
            IsEnabled = _profileService is not null,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
            ToolTip = "When a row's profile_name doesn't match an existing profile, " +
                      "create a new profile with that name and bind the cred to it. " +
                      "If profile_name is not mapped (or blank), every row binds to the " +
                      "'Default profile' below.",
        };
        _autoCreateProfile.Checked   += (_, _) => UpdatePreview();
        _autoCreateProfile.Unchecked += (_, _) => UpdatePreview();
        Grid.SetColumn(_autoCreateProfile, 0);
        profCreateRow.Children.Add(_autoCreateProfile);

        var defLabel = new TextBlock
        {
            Text = "Default profile name:",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        defLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetColumn(defLabel, 1);
        profCreateRow.Children.Add(defLabel);

        _defaultProfileName = new TextBox
        {
            Padding = new Thickness(6, 4, 6, 4),
            ToolTip = "Template applied to every row that lacks its own profile_name. " +
                      "Supports {{email}}, {{count}}, {{row}}, {{name}}, {{date}}, plus any " +
                      "mapped column / custom field name (e.g. {{username}}).",
        };
        _defaultProfileName.TextChanged += (_, _) => UpdatePreview();
        Grid.SetColumn(_defaultProfileName, 2);
        profCreateRow.Children.Add(_defaultProfileName);

        _skipExisting = new CheckBox
        {
            Content = "Skip if vault item already exists",
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            ToolTip = "When on, rows whose (Name, Kind, ProfileName) match an " +
                      "already-stored vault entry are skipped. Use this for " +
                      "idempotent re-imports — turn off when you genuinely " +
                      "want to add a second copy with refreshed secrets.",
        };
        _skipExisting.Checked   += (_, _) => UpdatePreview();
        _skipExisting.Unchecked += (_, _) => UpdatePreview();
        Grid.SetColumn(_skipExisting, 3);
        profCreateRow.Children.Add(_skipExisting);

        stack.Children.Add(profCreateRow);

        // Phase 71e — yellow callout block describing the
        // "Default profile name" template syntax. Users were
        // confused why no profiles got created when they left the
        // textbox empty; this block makes the templating obvious
        // and gives concrete examples.
        var tplHelp = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 12),
        };
        tplHelp.SetResourceReference(BorderBrushProperty, "WarnBrush");
        // Use a soft warn-tint background (10% opacity-ish via WarnSoftBrush
        // when available; falls back to the standard WarnBrush).
        try { tplHelp.SetResourceReference(BackgroundProperty, "WarnSoftBrush"); }
        catch { tplHelp.SetResourceReference(BackgroundProperty, "BgRaised"); }

        var tplStack = new StackPanel();
        var tplTitle = new TextBlock
        {
            Text = "💡  Profile-name template variables",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4),
        };
        tplTitle.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        tplStack.Children.Add(tplTitle);

        var tplBody = new TextBlock
        {
            Text =
                "Default profile name accepts placeholders that are substituted per row:\n" +
                "   {{email}}     — value of the email column for this row\n" +
                "   {{name}}      — the resolved Name (after Name-from picker)\n" +
                "   {{count}}     — sequential counter starting at 1\n" +
                "   {{row}}       — original CSV row number (1-indexed)\n" +
                "   {{date}}      — import timestamp (MMdd-HHmm)\n" +
                "   {{<column>}}  — any mapped column / custom field key\n" +
                "                    e.g. {{username}}, {{discord_token}}\n" +
                "Example:  acme-{{email}}-{{count}}   →   acme-john@x.com-1, acme-jane@x.com-2, …\n" +
                "Leave the template blank when each row already supplies its own profile_name.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = (FontFamily?)Application.Current.Resources["FontMono"]
                         ?? new FontFamily("Consolas"),
        };
        tplBody.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        tplStack.Children.Add(tplBody);

        tplHelp.Child = tplStack;
        stack.Children.Add(tplHelp);

        // Phase 71d — Kind dropdown removed from the UI. All new items
        // are "universal" by definition (single record per profile,
        // arbitrary fields). _kindCombo still exists in code as a
        // hidden constant carrier so the call sites that read
        // _kindCombo.SelectedValue ("universal") keep working without
        // ifdef'ing every reference.
        _kindCombo = new ComboBox
        {
            ItemsSource = VaultKinds.Catalog,
            DisplayMemberPath = "Label",
            SelectedValuePath = "Id",
            SelectedValue = "universal",
            Visibility = Visibility.Collapsed,
        };

        // 3. Row range — section renamed (was "Item shape" which made
        // sense only when Kind was user-pickable). Now it's just the
        // optional row-range filter.
        stack.Children.Add(MakeSection("3.  Row range (optional)"));
        var shapeGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shapeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddOptLabel(shapeGrid, 0, "From row");
        _fromRow = new TextBox { Text = "1", Width = 60, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 6, 0) };
        _fromRow.TextChanged += (_, _) => UpdatePreview();
        Grid.SetColumn(_fromRow, 1);
        shapeGrid.Children.Add(_fromRow);

        var toLabel = new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        toLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetColumn(toLabel, 2);
        shapeGrid.Children.Add(toLabel);

        _toRow = new TextBox { Text = "9999", Width = 70, Padding = new Thickness(6, 4, 6, 4) };
        _toRow.TextChanged += (_, _) => UpdatePreview();
        Grid.SetColumn(_toRow, 3);
        shapeGrid.Children.Add(_toRow);

        stack.Children.Add(shapeGrid);

        // Phase 71d — Name source picker. Dedicated row so the user
        // can pick which CSV column supplies each imported item's
        // Name (label) without having to find the right per-row
        // dropdown in the mapping table below.
        var nameRow = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AddOptLabel(nameRow, 0, "Name from");
        _nameSourceCombo = new ComboBox
        {
            Margin = new Thickness(0, 0, 12, 0),
            ToolTip = "Pick which CSV column supplies each imported item's Name. " +
                      "Leave on \"(auto-generate)\" to get \"Universal row N\" labels.",
        };
        _nameSourceCombo.SelectionChanged += (_, _) => UpdatePreview();
        Grid.SetColumn(_nameSourceCombo, 1);
        nameRow.Children.Add(_nameSourceCombo);

        var nameHelp = new TextBlock
        {
            Text = "If unset, items are named \"Universal row N · MMdd-HHmm\". Pick e.g. " +
                   "the username column for human-readable labels.",
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        nameHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        Grid.SetColumn(nameHelp, 2);
        nameRow.Children.Add(nameHelp);

        stack.Children.Add(nameRow);

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

    /// <summary>
    /// Phase 71d — repopulate <see cref="_nameSourceCombo"/> from the
    /// current CSV state. First entry is always "(auto-generate)" so
    /// the user can reset to the default behaviour with one click.
    /// Subsequent entries are the column headers (or "Column N" if
    /// no header row). Preserves the previous selection BY TEXT so
    /// the user's choice survives reparsing if the same column
    /// header is still present.
    /// </summary>
    private void RebuildNameSourceItems()
    {
        // Phase 71e bug fix — earlier version added bare header
        // strings via ComboBox.Items.Add(_rows[0][col]). When the
        // header row contained empty cells (Google Sheets exports
        // routinely pad with blank columns; or "First row is header"
        // is on but row 0 is the wrong row) the dropdown ended up
        // with several empty items that visually rendered as ONE
        // blank row. Now every column is guaranteed a non-empty,
        // distinguishable display string of shape:
        //
        //   "<header>  (col N)"   — when the header cell has text
        //   "Column N"            — when the header is empty / hasHeader=off
        //
        // The "(col N)" suffix also guarantees uniqueness so two
        // identically-named columns don't collapse in the list.
        var prev = _nameSourceCombo.SelectedItem as string;
        _nameSourceCombo.Items.Clear();
        _nameSourceCombo.Items.Add("(auto-generate)");
        if (_rows.Count == 0) { _nameSourceCombo.SelectedIndex = 0; return; }

        var colCount = _rows.Max(r => r.Count);
        var headerRow = _rows.Count > 0 ? _rows[0] : new List<string>();
        for (var col = 0; col < colCount; col++)
        {
            string raw = "";
            if (_hasHeader.IsChecked == true && col < headerRow.Count)
                raw = (headerRow[col] ?? "").Trim();

            var display = string.IsNullOrEmpty(raw)
                ? $"Column {col + 1}"
                : $"{raw}  (col {col + 1})";
            _nameSourceCombo.Items.Add(display);
        }

        if (prev is not null && _nameSourceCombo.Items.Contains(prev))
            _nameSourceCombo.SelectedItem = prev;
        else
            _nameSourceCombo.SelectedIndex = 0;
    }

    private string[] BuildChoiceList()
    {
        var kind = (_kindCombo?.SelectedValue as string) ?? "universal";
        var spec = VaultKinds.Get(kind);
        var fields = spec?.Fields ?? Array.Empty<string>();
        var list = new List<string>(BaseChoices);
        foreach (var f in fields)
            if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        // Phase 70b — append session-scoped custom fields (sorted),
        // then the "+ Add…" sentinel that opens the field-name prompt.
        foreach (var f in _customFields.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            if (!list.Contains(f, StringComparer.OrdinalIgnoreCase)) list.Add(f);
        list.Add(CustomFieldSentinel);
        return list.ToArray();
    }

    /// <summary>
    /// Phase 70b — sentinel handler. Pops a tiny modal asking the
    /// user for a new field name, normalises it (snake_case,
    /// lowercased, leading-digit-guarded), adds it to
    /// <see cref="_customFields"/>, then rebuilds every dropdown so
    /// the new key shows up everywhere — and selects it on the
    /// triggering combo.
    /// </summary>
    private void OnCustomFieldRequested(ComboBox triggeringCombo)
    {
        // Phase 71 — share the universal editor's CustomFieldDialog so
        // bulk-imported custom fields can be marked encrypted / TOTP at
        // creation time. The dialog returns a normalised name + flags;
        // we stash the flags in _customFieldMeta so the commit pass
        // can route values to encrypted vs. extras_json correctly.
        var prompt = new CustomFieldDialog { Owner = this };
        var ok = prompt.ShowDialog() == true;
        var name = NormaliseFieldName(prompt.FieldName);
        if (!ok || string.IsNullOrEmpty(name))
        {
            // Revert to (skip) so the combo doesn't sit on the sentinel.
            triggeringCombo.SelectedItem = "(skip)";
            return;
        }
        // is_totp implies encrypted — never store a TOTP seed plaintext.
        var encrypted = prompt.Encrypted || prompt.IsTotp;
        _customFieldMeta[name] = new VaultFieldMeta
        {
            Encrypted = encrypted,
            IsTotp    = prompt.IsTotp,
        };

        // Reject collisions with reserved (BaseChoices / kind fields)
        // — those are already in the list, no need to dupe.
        var reserved = new HashSet<string>(BaseChoices, StringComparer.OrdinalIgnoreCase);
        var spec = VaultKinds.Get((_kindCombo?.SelectedValue as string) ?? "universal");
        if (spec is not null)
            foreach (var f in spec.Fields) reserved.Add(f);
        if (reserved.Contains(name))
        {
            // Already a built-in — just select it.
            triggeringCombo.SelectedItem = name;
            return;
        }
        if (!_customFields.Contains(name, StringComparer.OrdinalIgnoreCase))
            _customFields.Add(name);

        // Rebuild all combos to surface the new key everywhere.
        // Preserve current selections by snapshotting first.
        var prevSelections = _mappingCombos.Select(c => c.SelectedItem as string).ToList();
        RebuildMappingDropdowns(refreshPreviewToo: false);
        for (var i = 0; i < _mappingCombos.Count && i < prevSelections.Count; i++)
        {
            var prev = prevSelections[i];
            if (prev is null || prev == CustomFieldSentinel) continue;
            // Restore prior selection if it still exists in the choice list.
            var combo = _mappingCombos[i];
            if (combo.Items.Contains(prev))
                combo.SelectedItem = prev;
        }
        // Set the triggering combo to the freshly added name.
        var idx = _mappingCombos.IndexOf(triggeringCombo);
        if (idx >= 0)
        {
            // After rebuild, _mappingCombos[idx] is a NEW combo instance;
            // find by column position and assign the new key.
            _mappingCombos[idx].SelectedItem = name;
        }
        UpdatePreview();
    }

    /// <summary>
    /// Phase 70b — defensive normalisation for user-typed field
    /// names. Lowercases, replaces non-alphanumerics with '_', strips
    /// duplicate/leading/trailing underscores, prefixes a leading
    /// digit with 'f_' so the resulting key is a valid placeholder
    /// identifier (matches the <c>{{vault.X}}</c> regex
    /// <c>[A-Za-z_][A-Za-z0-9_]*</c>).
    /// </summary>
    internal static string NormaliseFieldName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();

        // Phase 70c audit fix — PascalCase / camelCase → snake_case
        // pre-pass. "EmailPassword" → "email_password" instead of the
        // previous "emailpassword". Insert '_' between a lowercase /
        // digit and an uppercase, and between consecutive uppercase
        // followed by lowercase ("HTTPRequest" → "http_request").
        var pre = new StringBuilder(trimmed.Length + 8);
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (i > 0 && char.IsUpper(ch))
            {
                var prev = trimmed[i - 1];
                var next = i + 1 < trimmed.Length ? trimmed[i + 1] : '\0';
                if (char.IsLower(prev) || char.IsDigit(prev) ||
                    (char.IsUpper(prev) && next != '\0' && char.IsLower(next)))
                {
                    pre.Append('_');
                }
            }
            pre.Append(ch);
        }

        var sb = new StringBuilder(pre.Length);
        foreach (var ch in pre.ToString().ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '_')
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || ch == '-' || ch == '.')
                sb.Append('_');
            // anything else → drop
        }
        var s = sb.ToString();
        // Collapse repeated underscores.
        while (s.Contains("__")) s = s.Replace("__", "_");
        s = s.Trim('_');
        if (s.Length == 0) return "";
        if (s[0] >= '0' && s[0] <= '9') s = "f_" + s;
        return s;
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
            // Phase 71d — clear the Name-source picker too so a stale
            // header from a previous CSV doesn't linger.
            RebuildNameSourceItems();
            UpdatePreview();
            return;
        }

        var choices = BuildChoiceList();
        var colCount = _rows.Max(r => r.Count);

        // Phase 71d — refresh Name-source items every parse so the
        // user sees the current CSV's column headers as choices.
        RebuildNameSourceItems();

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
            combo.SelectionChanged += (s, _) =>
            {
                // Phase 70b — sentinel intercepts: opening the input
                // dialog at SelectionChanged time would re-enter WPF's
                // selection logic if we don't defer it via Dispatcher.
                if (s is ComboBox c && (c.SelectedItem as string) == CustomFieldSentinel)
                {
                    Dispatcher.BeginInvoke(new Action(() => OnCustomFieldRequested(c)));
                    return;
                }
                UpdatePreview();
            };
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

        // Phase 71d — fuzzy alias map pruned to ONLY the canonical
        // universal-vault fields the user explicitly listed:
        //   name · email · email_password · profile_name · status · tags · notes
        // Anything else (username/password/totp/seed/discord_token/…)
        // is opt-in via the "+ Add custom field…" sentinel; users
        // pick the encrypted/IsTotp flags per field at that point.
        // ORDER MATTERS for the substring fallback at the bottom:
        // more specific aliases must come BEFORE general ones
        // (e.g. "emailpassword" must beat "email").
        var aliases = new (string col, string field)[]
        {
            ("profile",        "profile_name"),
            ("profilename",    "profile_name"),
            ("profile_name",   "profile_name"),
            ("emailpassword",  "email_password"),
            ("email_password", "email_password"),
            ("emailpass",      "email_password"),
            ("mailpass",       "email_password"),
            ("mailpassword",   "email_password"),
            ("email",          "email"),
            ("mail",           "email"),
            ("name",           "name"),
            ("label",          "name"),
            ("notes",          "notes"),
            ("note",           "notes"),
            ("comment",        "notes"),
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

        var kind = (_kindCombo?.SelectedValue as string) ?? "universal";
        var profileNames = _profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var autoCreate = _autoCreateProfile.IsChecked == true && _profileService is not null;
        var defaultProf = (_defaultProfileName.Text ?? "").Trim();

        // Phase 70c audit fix — warn the user when two columns are
        // mapped to the same target. Currently the loop in MapRow does
        // last-write-wins (later column overwrites earlier value)
        // silently — the warning surfaces the data loss before commit.
        var dupTargets = _mappingCombos
            .Select(c => c.SelectedItem as string)
            .Where(t => t is not null && t != "(skip)" && t != CustomFieldSentinel)
            .GroupBy(t => t!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupTargets.Count > 0)
        {
            var d = string.Join(", ", dupTargets);
            ShowError($"Two columns map to the same field(s): {d}. " +
                      "Only the last column's value will be saved — adjust mapping to avoid data loss.");
        }
        else
        {
            ClearError();
        }

        var errors = 0;
        var warnings = 0;
        var dataIdx = 0;
        // Phase 71e — counter for {{count}} template variable.
        // Increments only for rows that actually pass the
        // empty-row + range filters, so the value reflects the
        // staged-row sequence and not raw CSV indices.
        var stagedSeq = 0;
        for (var i = dataStart; i < _rows.Count; i++)
        {
            dataIdx++;
            if (dataIdx < fromN) continue;
            if (dataIdx > toN) break;

            var raw = _rows[i];
            // Skip fully-empty rows silently — Google Sheets exports
            // are notorious for trailing-blank-row padding.
            if (raw.All(string.IsNullOrWhiteSpace)) continue;

            stagedSeq++;
            var staged = MapRow(raw, kind);
            staged.OriginalRowNumber = i + 1; // 1-indexed including header

            // Phase 71e — apply the default-profile-name TEMPLATE
            // (was a literal string before). If the row didn't bring
            // its own profile_name and the user typed a template,
            // substitute placeholders ({{email}} / {{count}} / …) per
            // row. Each row may end up bound to a DIFFERENT generated
            // profile if the template includes per-row variables —
            // which is the workflow the user asked for ("acme-{{email}}-
            // {{count}}-test").
            if (string.IsNullOrEmpty(staged.Item.ProfileName) &&
                !string.IsNullOrEmpty(defaultProf))
            {
                var resolved = ApplyProfileNameTemplate(defaultProf, staged, stagedSeq);
                if (!string.IsNullOrEmpty(resolved))
                    staged.Item = staged.Item with { ProfileName = resolved };
            }

            // Phase 70 — last-resort name synthesis. By now name is
            // either user-mapped, derived from nickname/identifier/profile,
            // or still empty for completely unannotated rows. Stamp a
            // generic "<KindLabel> row N · <timestamp>" so we never drop
            // a row purely because of missing label AND a same-CSV
            // re-import doesn't collide with the previous import's
            // fallback names (Phase 70c audit fix). Timestamp is the
            // import's start time, not per-row, so all rows in one
            // batch share the suffix and stay grouped.
            if (string.IsNullOrWhiteSpace(staged.Item.Name))
            {
                staged.Item = staged.Item with
                {
                    Name = $"{KindLabel(kind)} row {staged.OriginalRowNumber} · {_importStamp}",
                };
            }

            // Validation. Note: we no longer reject rows for missing
            // 'name' (auto-named above) or unknown profile (auto-created
            // at commit time below). The only hard reject is the legacy
            // skipEmptyProfile path which the user can still opt into.
            string? err = null;
            string? warn = null;

            var hasProfile = !string.IsNullOrEmpty(staged.Item.ProfileName);
            var profileExists = hasProfile && profileNames.Contains(staged.Item.ProfileName!);

            if (_skipEmptyProfile.IsChecked == true && !autoCreate)
            {
                if (!hasProfile)
                    err = "row has no profile_name (Skip-no-profile is on)";
                else if (!profileExists)
                    err = $"profile '{staged.Item.ProfileName}' doesn't exist (Skip-no-profile is on)";
            }
            else if (hasProfile && !profileExists)
            {
                warn = autoCreate
                    ? $"profile '{staged.Item.ProfileName}' will be created"
                    : $"profile '{staged.Item.ProfileName}' not found — saved as unbound";
            }

            if (err is not null) { staged.Error = err; errors++; }
            else if (warn is not null) { staged.Warning = warn; warnings++; }

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
        string? profile = null;
        string? email = null;
        // Phase 71 — accumulate plaintext custom field values for
        // extras_json. Keys whose VaultFieldMeta.Encrypted=false land
        // here instead of in `secrets` (which is encrypted at insert
        // time). This is the persistence-side counterpart to the
        // editor's per-field encrypted/plaintext routing.
        var plainExtras = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var col = 0; col < _mappingCombos.Count && col < raw.Count; col++)
        {
            var target = (_mappingCombos[col].SelectedItem as string) ?? "(skip)";
            if (target == "(skip)") continue;
            var val = (raw[col] ?? "").Trim();
            if (string.IsNullOrEmpty(val)) continue;

            // Phase 71d — pruned switch. Only the canonical universal
            // fields get top-level handling; everything else (any
            // custom-added key, including legacy nickname/identifier/
            // service if a user manually re-adds them) routes through
            // the meta-aware default arm and lands in either secrets
            // (encrypted, default) or plainExtras (explicit plaintext).
            switch (target)
            {
                case "profile_name": profile = val; break;
                case "name":         name    = val; break;
                case "notes":        notes   = val; break;
                case "tags":         tags    = val; break;
                case "status":       status  = val; break;
                case "email":
                    // Email is a top-level plaintext column, not a secret.
                    // Searchable from the list view.
                    email = val;
                    break;
                default:
                    var meta = _customFieldMeta.TryGetValue(target!, out var m)
                        ? m : VaultFieldMeta.Default;
                    if (meta.Encrypted || meta.IsTotp)
                        secrets[target!] = val;
                    else
                        plainExtras[target!] = val;
                    break;
            }
        }

        // Phase 71d — Name-source picker takes precedence over the
        // column mapping when set. SelectedIndex 0 = "(auto-generate)",
        // which means "use the existing fallback chain". Anything
        // higher means "use raw value from that CSV column".
        var nameSrcIdx = _nameSourceCombo?.SelectedIndex ?? 0;
        if (nameSrcIdx > 0)
        {
            // -1 because index 0 is "(auto-generate)"; CSV columns
            // start at index 1 in the combo.
            var srcCol = nameSrcIdx - 1;
            if (srcCol < raw.Count)
            {
                var v = (raw[srcCol] ?? "").Trim();
                if (!string.IsNullOrEmpty(v))
                    name = v;
            }
        }

        // Phase 71d — auto-fill name when not mapped (and the
        // Name-source picker didn't override). Chain:
        //   email → "<KindLabel> · profile" → "<KindLabel> row N"
        //   (last-resort, set by UpdatePreview).
        if (string.IsNullOrEmpty(name))
        {
            if (!string.IsNullOrEmpty(email))
                name = email;
            else if (!string.IsNullOrEmpty(profile))
                name = $"{KindLabel(kind)} · {profile}";
        }

        // Phase 71 — surface custom-field metadata on the staged item
        // so the commit pass writes field_meta_json / extras_json
        // alongside the encrypted secrets blob. Only fields that
        // actually appear in this row's mapping (i.e. used at least
        // once) get persisted on the item — keeps field_meta_json
        // small for typical 1–5-field rows.
        Dictionary<string, VaultFieldMeta>? perItemMeta = null;
        foreach (var key in secrets.Keys.Concat(plainExtras.Keys))
        {
            if (_customFieldMeta.TryGetValue(key, out var m))
            {
                perItemMeta ??= new Dictionary<string, VaultFieldMeta>(StringComparer.Ordinal);
                perItemMeta[key] = m;
            }
        }
        var fieldMetaJson = perItemMeta is null
            ? null
            : System.Text.Json.JsonSerializer.Serialize(perItemMeta);
        var extrasJson = plainExtras.Count == 0
            ? null
            : System.Text.Json.JsonSerializer.Serialize(plainExtras);

        item = item with
        {
            Name          = name ?? "",
            Kind          = kind,
            // Phase 71d — Identifier / Service no longer mappable from
            // bulk import; left as null on universal items. Legacy
            // values on existing items are preserved by their own
            // editor flow, never touched here.
            Identifier    = null,
            Service       = null,
            Email         = string.IsNullOrEmpty(email) ? null : email,
            ProfileName   = string.IsNullOrEmpty(profile) ? null : profile,
            Status        = !string.IsNullOrEmpty(status) && VaultKinds.IsValidStatus(status) ? status : "active",
            Notes         = notes,
            TagsJson      = string.IsNullOrEmpty(tags) ? null
                             : System.Text.Json.JsonSerializer.Serialize(
                                  tags.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                  | StringSplitOptions.TrimEntries)),
            FieldMetaJson = fieldMetaJson,
            ExtrasJson    = extrasJson,
        };
        return new ParsedRow { Item = item, Secrets = secrets };
    }

    private static string KindLabel(string kind) => VaultKinds.Get(kind)?.Label ?? kind;

    /// <summary>
    /// Phase 71e — substitute <c>{{var}}</c> placeholders in the
    /// Default-profile-name template with per-row values. Recognised
    /// variables (case-insensitive):
    ///
    ///   {{email}}, {{name}}, {{count}}, {{row}}, {{date}}
    ///
    /// Plus any key that exists in the staged item's secrets bag or
    /// extras_json — including custom fields the user added via
    /// "+ Add custom field…". Unknown variables are left literal so
    /// the user notices typos in their preview.
    ///
    /// The result is sanitised lightly (trim whitespace + collapse
    /// double-dashes) but NOT slugified — profile names accept
    /// arbitrary printable characters; we let the user pick the
    /// shape.
    /// </summary>
    private string ApplyProfileNameTemplate(string template, ParsedRow staged, int stagedSeq)
    {
        if (string.IsNullOrEmpty(template)) return "";

        // Build the substitution dictionary once per row. Lower-case
        // keys, case-insensitive lookups in the regex callback.
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = stagedSeq.ToString(),
            ["row"]   = staged.OriginalRowNumber.ToString(),
            ["date"]  = _importStamp,
            ["name"]  = staged.Item.Name ?? "",
            ["email"] = staged.Item.Email ?? "",
        };
        // Merge in secrets + extras values so {{username}}, {{discord_token}}
        // etc. resolve. Encrypted values are still cleartext at this
        // point in the flow (we haven't passed them to VaultService yet).
        foreach (var kv in staged.Secrets)
            vars[kv.Key] = kv.Value;

        var rx = new System.Text.RegularExpressions.Regex(
            @"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        var resolved = rx.Replace(template, m =>
        {
            var key = m.Groups[1].Value;
            return vars.TryGetValue(key, out var v) ? v : m.Value; // leave literal on miss
        });

        // Tidy up: collapse double-dashes / spaces produced by an
        // empty {{var}} match in the middle of the template
        // (e.g. template "x-{{email}}-y" with no email column → "x--y").
        while (resolved.Contains("--")) resolved = resolved.Replace("--", "-");
        while (resolved.Contains("  ")) resolved = resolved.Replace("  ", " ");
        return resolved.Trim(' ', '-', '_');
    }

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
        var profilesCreated = 0;

        // Phase 70 — auto-create-profile pre-pass. Walk distinct
        // ProfileName values across ready rows and CreateAsync any
        // that don't exist yet, so the vault inserts below see a
        // consistent profile catalog. Done in one pass (not per-row
        // inside the loop) so we don't re-query ListAsync N times.
        var autoCreate = _autoCreateProfile.IsChecked == true && _profileService is not null;
        if (autoCreate)
        {
            try
            {
                var existing = await _profileService!.ListAsync();
                var existingNames = existing.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var needCreate = ready
                    .Select(r => r.Item.ProfileName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => !existingNames.Contains(n!))
                    .ToList();

                foreach (var pname in needCreate)
                {
                    try
                    {
                        await _profileService.CreateAsync(new Profile
                        {
                            Name = pname!,
                            EnrichOnFirstRun = true,
                            // Leave template/proxy unset — user can edit
                            // the profile later. The vault creds bind
                            // by name, not by template.
                        });
                        profilesCreated++;
                    }
                    catch (Exception ex)
                    {
                        // Phase 70c audit fix — race window between
                        // ListAsync (pre-pass) and CreateAsync: another
                        // caller (concurrent dialog, scheduler) may
                        // have created the same name in between.
                        // Re-check existence and treat "already there"
                        // as success rather than surfacing a UNIQUE
                        // violation as an error to the user.
                        try
                        {
                            var maybeExisting = await _profileService.GetAsync(pname!);
                            if (maybeExisting is not null)
                            {
                                // Race resolved benignly — somebody else
                                // already created the profile. Bump the
                                // counter so the toast still shows the
                                // user-visible "+ N profiles created"
                                // tally that includes auto-resolved races.
                                profilesCreated++;
                                continue;
                            }
                        }
                        catch { /* re-check failed too — fall through to log */ }

                        // Non-fatal: vault item will still be created,
                        // it'll just save as unbound until the profile
                        // gets created manually.
                        failed.Add((0, $"could not auto-create profile '{pname}': {ex.Message}"));
                    }
                }

                // Refresh _profiles snapshot so subsequent dialog reuse
                // (if the user re-opens without closing) sees the new
                // catalogue.
                _profiles = await _profileService.ListAsync();
            }
            catch (Exception ex)
            {
                failed.Add((0, $"profile auto-create pre-pass failed: {ex.Message}"));
            }
        }

        // Phase 70c audit fix — pre-fetch existing vault items grouped
        // by (Name, Kind, ProfileName) so the loop can dedup in O(1)
        // when "Skip if vault item already exists" is on. We snapshot
        // ALL items (kind=null) once instead of per-row to avoid
        // O(N²) queries against the vault for large imports.
        HashSet<string>? existingTuples = null;
        var skipExisting = _skipExisting.IsChecked == true;
        if (skipExisting)
        {
            try
            {
                var allItems = await _vault.ListAsync();
                existingTuples = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in allItems)
                    existingTuples.Add($"{v.Name}|{v.Kind}|{v.ProfileName}");
            }
            catch (Exception ex)
            {
                failed.Add((0, $"could not pre-fetch vault for dedup: {ex.Message}"));
                existingTuples = null; // fall back to non-skipping behaviour
            }
        }

        var skippedDup = 0;
        try
        {
            foreach (var r in ready)
            {
                if (existingTuples is not null)
                {
                    var key = $"{r.Item.Name}|{r.Item.Kind}|{r.Item.ProfileName}";
                    if (existingTuples.Contains(key))
                    {
                        skippedDup++;
                        continue;
                    }
                    // Add the new tuple so two CSV rows with identical
                    // synthesised names within the same import don't
                    // both insert and produce a same-batch dupe.
                    existingTuples.Add(key);
                }
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
            // Stash the profile-creation count so the caller can show
            // a more accurate toast ("imported 50 creds + 12 profiles").
            CreatedProfilesCount = profilesCreated;
            if (failed.Count > 0 || skippedDup > 0)
            {
                var msg = $"Imported {created} of {ready.Count}.";
                if (skippedDup > 0)
                    msg += $" Skipped {skippedDup} duplicate(s).";
                if (failed.Count > 0)
                {
                    msg += " Errors:\n" +
                           string.Join("\n", failed.Take(10)
                               .Select(f => $"  · row {f.row}: {f.err}"));
                    if (failed.Count > 10) msg += $"\n  · …and {failed.Count - 10} more";
                }
                MessageBox.Show(this, msg, "Bulk import — summary",
                    MessageBoxButton.OK,
                    failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
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

/// <summary>
/// Phase 70b — minimal modal input dialog. Used by the bulk-import
/// dialog's "(custom field…)" flow to prompt the user for a new
/// secret-key name. Generic enough to reuse elsewhere if needed
/// (kept internal so it can grow without polluting the public
/// dialog surface).
///
/// Tiny on purpose: Title, Prompt label, single TextBox, OK/Cancel.
/// Validation is the caller's responsibility — we just return the
/// raw text via <see cref="Value"/>.
/// </summary>
internal sealed class InputDialog : Window
{
    public string Prompt { get; init; } = "";
    public string DefaultValue { get; init; } = "";
    public string Value { get; private set; } = "";

    private readonly TextBox _input;

    public InputDialog()
    {
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        var root = new StackPanel { Margin = new Thickness(20) };

        var promptBlock = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        promptBlock.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        promptBlock.SetBinding(TextBlock.TextProperty,
            new System.Windows.Data.Binding(nameof(Prompt)) { Source = this });
        root.Children.Add(promptBlock);

        _input = new TextBox
        {
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 14),
        };
        root.Children.Add(_input);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        ok.SetResourceReference(StyleProperty, "ButtonPrimary");
        ok.Click += (_, _) => { Value = _input.Text ?? ""; DialogResult = true; Close(); };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        root.Children.Add(btns);

        Content = root;
        Loaded += (_, _) =>
        {
            _input.Text = DefaultValue ?? "";
            _input.SelectAll();
            _input.Focus();
        };
    }
}

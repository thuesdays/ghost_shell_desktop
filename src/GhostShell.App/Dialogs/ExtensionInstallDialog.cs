// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GhostShell.Core.Extensions;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Win32;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 27 — install dialog with FOUR install paths in tabs:
///   • From file       — pick a .zip / .crx
///   • From folder     — pick an unpacked extension dir
///   • Store catalog   — search the curated list, click to install
///   • Custom URL/ID   — paste a CWS link or 32-char ID
/// </summary>
public sealed class ExtensionInstallDialog : Window
{
    public ExtensionItem? Installed { get; private set; }
    private readonly IExtensionService _service;

    /// <summary>Status banner — full-width block above the close row.
    /// Replaces the inline status label (which got clipped under the
    /// Close button at narrow widths and faded into the page chrome
    /// instead of catching the eye on errors).</summary>
    private readonly Border _statusBanner;
    private readonly TextBlock _statusLabel;

    public ExtensionInstallDialog(IExtensionService service)
    {
        _service = service;
        Title = "Install extension";
        Width = 720; Height = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // header
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // tabs
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // status banner
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // close button

        // Header
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = "🧩  Add extension", FontSize = 16, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        head.Children.Add(title);
        var sub = new TextBlock
        {
            Text = "Pick how you want to install. The extension is unpacked under %LocalAppData%\\GhostShell\\extensions and loaded into every profile by default — flip global or per-profile state later.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        head.Children.Add(sub);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // Tabs
        var tabs = new TabControl();
        tabs.Items.Add(BuildFileTab());
        tabs.Items.Add(BuildFolderTab());
        tabs.Items.Add(BuildStoreTab());
        tabs.Items.Add(BuildCustomTab());
        Grid.SetRow(tabs, 1);
        root.Children.Add(tabs);

        // Status banner — its own row above the close button. Stays
        // collapsed when there's nothing to say, expands into a full-
        // width coloured block on install start / success / failure
        // so the user can't miss it (especially errors, which used to
        // hide under the Close button at narrow widths).
        _statusLabel = new TextBlock
        {
            Text = "",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
        };
        _statusBanner = new Border
        {
            Padding = new Thickness(14, 10, 14, 10),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 16, 0, 0),
            Visibility = Visibility.Collapsed,
            Child = _statusLabel,
        };
        Grid.SetRow(_statusBanner, 2);
        root.Children.Add(_statusBanner);

        // Close row — its own row beneath the banner.
        var closeRow = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        closeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        closeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var close = new Button { Content = "Close", MinWidth = 84, IsCancel = true };
        close.Click += (_, _) => { DialogResult = Installed is not null; Close(); };
        Grid.SetColumn(close, 1);
        closeRow.Children.Add(close);
        Grid.SetRow(closeRow, 3);
        root.Children.Add(closeRow);

        Content = root;
    }

    // ─── File (.zip / .crx) ────────────────────────────────────────────

    private TabItem BuildFileTab()
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(MakeLabel("Pick a .zip or .crx file"));
        var help = new TextBlock
        {
            Text = "Best for extensions you've downloaded as a packaged file. The archive is unpacked into our managed dir; you can keep or delete the original.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(help);

        var pickBtn = new Button { Content = "📁  Choose file…", MinWidth = 160 };
        pickBtn.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Chrome extensions (*.zip;*.crx)|*.zip;*.crx|All files|*.*",
                Multiselect = false,
            };
            if (dlg.ShowDialog() == true)
                await DoInstallAsync(() => _service.InstallFromZipAsync(dlg.FileName));
        };
        stack.Children.Add(pickBtn);

        return new TabItem { Header = "📁 From file", Content = stack };
    }

    // ─── Folder (unpacked) ────────────────────────────────────────────

    private TabItem BuildFolderTab()
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(MakeLabel("Pick an unpacked extension folder"));
        var help = new TextBlock
        {
            Text = "Best when you're developing your own extension or have a folder that contains manifest.json. We copy the folder so the original stays untouched.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(help);

        var folderBox = new TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(folderBox, 0);
        row.Children.Add(folderBox);

        var browseBtn = new Button { Content = "Browse…", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        browseBtn.Click += (_, _) =>
        {
            // OpenFolderDialog landed in .NET 8 / WPF .NET 8 — fallback
            // to picking any file in the folder if not available.
            var ofd = new OpenFolderDialog();
            if (ofd.ShowDialog() == true) folderBox.Text = ofd.FolderName;
        };
        Grid.SetColumn(browseBtn, 1);
        row.Children.Add(browseBtn);
        stack.Children.Add(row);

        var installBtn = new Button { Content = "Install", MinWidth = 120, Margin = new Thickness(0, 8, 0, 0) };
        installBtn.SetResourceReference(StyleProperty, "ButtonPrimary");
        installBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(folderBox.Text))
            {
                ShowStatus("Pick a folder first.", isError: true);
                return;
            }
            await DoInstallAsync(() => _service.InstallFromFolderAsync(folderBox.Text));
        };
        stack.Children.Add(installBtn);

        return new TabItem { Header = "📂 From folder", Content = stack };
    }

    // ─── Store catalog ────────────────────────────────────────────────

    private TabItem BuildStoreTab()
    {
        var grid = new Grid { Margin = new Thickness(14) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var search = new TextBox { Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(search, 0);
        grid.Children.Add(search);

        // Empty-state hint shown when search returns zero rows. Points
        // the user at the Custom URL tab so they don't think the app
        // is broken when an extension we don't curate is missing.
        var emptyHint = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
        };
        emptyHint.SetResourceReference(Border.BackgroundProperty, "BgDeep");
        emptyHint.SetResourceReference(Border.BorderBrushProperty, "Border");
        emptyHint.BorderThickness = new Thickness(1);
        var emptyText = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Text = "No matches in the curated catalog. The catalog is hand-picked — for anything else, switch to the \"🔗 Custom URL\" tab and paste the Chrome Web Store link or 32-char extension ID. Any extension on chromewebstore.google.com works.",
        };
        emptyText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        emptyHint.Child = emptyText;
        Grid.SetRow(emptyHint, 1);
        grid.Children.Add(emptyHint);

        var listBox = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            // Explicit alignment values silence WPF's default-style
            // FindAncestor bindings for HorizontalContentAlignment /
            // VerticalContentAlignment that fail with "Cannot find
            // source for binding" when ListBoxItem evaluates them
            // before the visual tree is fully connected. The bindings
            // resolve themselves once the popup hosts the item but
            // not before WPF logs the error to the trace listener.
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        // Attached property — has to be set via the static helper, not
        // an initializer (initializers can only set instance members).
        ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
        var entries = new ObservableCollection<ExtensionStoreEntry>();
        listBox.ItemsSource = entries;

        var template = new DataTemplate(typeof(ExtensionStoreEntry));
        var card = new FrameworkElementFactory(typeof(Border));
        card.SetResourceReference(Border.BackgroundProperty, "BgDeep");
        card.SetResourceReference(Border.BorderBrushProperty, "Border");
        card.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        card.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        card.SetValue(Border.PaddingProperty, new Thickness(12, 8, 12, 8));
        card.SetValue(Border.MarginProperty, new Thickness(0, 0, 0, 6));

        var inner = new FrameworkElementFactory(typeof(Grid));
        var col0 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col0.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
        var col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
        col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
        inner.AppendChild(col0);
        inner.AppendChild(col1);

        var info = new FrameworkElementFactory(typeof(StackPanel));
        info.SetValue(Grid.ColumnProperty, 0);
        var nameTxt = new FrameworkElementFactory(typeof(TextBlock));
        nameTxt.SetValue(TextBlock.FontSizeProperty, 13.0);
        nameTxt.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        nameTxt.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        info.AppendChild(nameTxt);
        var desc = new FrameworkElementFactory(typeof(TextBlock));
        desc.SetValue(TextBlock.FontSizeProperty, 11.0);
        desc.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        desc.SetValue(TextBlock.MarginProperty, new Thickness(0, 2, 0, 0));
        desc.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        desc.SetBinding(TextBlock.TextProperty, new Binding("Description"));
        info.AppendChild(desc);
        inner.AppendChild(info);

        var btn = new FrameworkElementFactory(typeof(Button));
        btn.SetValue(Button.ContentProperty, "⬇  Install");
        btn.SetValue(Button.MinWidthProperty, 100.0);
        btn.SetValue(Grid.ColumnProperty, 1);
        btn.SetValue(Button.MarginProperty, new Thickness(12, 0, 0, 0));
        btn.SetValue(Button.VerticalAlignmentProperty, VerticalAlignment.Center);
        btn.SetResourceReference(Button.StyleProperty, "ButtonPrimary");
        btn.AddHandler(Button.ClickEvent, new RoutedEventHandler(async (s, _) =>
        {
            if (s is Button b && b.DataContext is ExtensionStoreEntry e)
                await DoInstallAsync(() => _service.InstallFromStoreAsync(e.ExtId));
        }));
        inner.AppendChild(btn);

        card.AppendChild(inner);
        template.VisualTree = card;
        listBox.ItemTemplate = template;

        Grid.SetRow(listBox, 2);
        grid.Children.Add(listBox);

        // Initial load: full curated catalog.
        async Task RefreshAsync()
        {
            entries.Clear();
            var res = await _service.GetCuratedCatalogAsync(search.Text);
            foreach (var e in res) entries.Add(e);
            // Phase 27 — show the "use Custom URL" hint only when the
            // user typed a non-empty query that returned no curated
            // results. Empty query + empty catalog (which can't happen
            // in practice) skips the hint to avoid confusing first run.
            emptyHint.Visibility =
                entries.Count == 0 && !string.IsNullOrWhiteSpace(search.Text)
                    ? Visibility.Visible : Visibility.Collapsed;
        }
        _ = RefreshAsync();
        search.TextChanged += async (_, _) => await RefreshAsync();

        return new TabItem { Header = "🏪 Store", Content = grid };
    }

    // ─── Custom URL / ID ──────────────────────────────────────────────

    private TabItem BuildCustomTab()
    {
        var stack = new StackPanel { Margin = new Thickness(14) };
        stack.Children.Add(MakeLabel("Paste a Chrome Web Store link or 32-char ID"));
        var help = new TextBlock
        {
            Text = "Examples:\n  • https://chromewebstore.google.com/detail/extension-name/cjpalhdlnbpafiamejdnhcphjbkeiagm\n  • cjpalhdlnbpafiamejdnhcphjbkeiagm",
            FontSize = 10,
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Code, Consolas"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };
        help.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        stack.Children.Add(help);

        var box = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(box);

        var btn = new Button { Content = "⬇  Download + install", MinWidth = 200 };
        btn.SetResourceReference(StyleProperty, "ButtonPrimary");
        btn.Click += async (_, _) =>
        {
            var id = ExtractCwsId(box.Text);
            if (string.IsNullOrEmpty(id))
            {
                ShowStatus("Couldn't find a 32-char extension ID in that input.", isError: true);
                return;
            }
            await DoInstallAsync(() => _service.InstallFromStoreAsync(id));
        };
        stack.Children.Add(btn);

        return new TabItem { Header = "🔗 Custom URL", Content = stack };
    }

    // ─── Plumbing ─────────────────────────────────────────────────────

    private async Task DoInstallAsync(Func<Task<ExtensionItem>> work)
    {
        ShowStatus("Installing…", isError: false);
        try
        {
            var item = await work();
            Installed = item;
            ShowStatus($"✓  {item.Name} v{item.Version} installed.", isError: false);
            // Phase 27 audit fix — auto-close after a short delay so
            // the user doesn't have to click "Close" themselves. Long
            // enough to read the success line, short enough not to feel
            // sluggish.
            await Task.Delay(900);
            if (IsLoaded) // dialog still open?
            {
                DialogResult = true;
                Close();
            }
        }
        catch (Exception ex)
        {
            ShowStatus("✗  " + ex.Message, isError: true);
        }
    }

    private void ShowStatus(string text, bool isError)
    {
        _statusLabel.Text = text;
        _statusBanner.Visibility = string.IsNullOrEmpty(text)
            ? Visibility.Collapsed : Visibility.Visible;
        // Tint the banner so errors jump off the page. Success stays
        // muted (we have the auto-close to confirm it). Errors get a
        // red foreground + a soft red border so they're impossible to
        // miss even when the user has already moved on.
        if (isError)
        {
            _statusLabel.SetResourceReference(TextBlock.ForegroundProperty, "ErrBrush");
            _statusBanner.SetResourceReference(Border.BorderBrushProperty, "ErrBrush");
            _statusBanner.SetResourceReference(Border.BackgroundProperty, "BgRaised");
        }
        else
        {
            _statusLabel.SetResourceReference(TextBlock.ForegroundProperty, "OkBrush");
            _statusBanner.SetResourceReference(Border.BorderBrushProperty, "OkBrush");
            _statusBanner.SetResourceReference(Border.BackgroundProperty, "BgRaised");
        }
    }

    private static TextBlock MakeLabel(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold,
                                Margin = new Thickness(0, 0, 0, 4) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return t;
    }

    /// <summary>Extract a 32-char Chrome extension ID from a CWS URL or
    /// a bare ID. Returns null if the input doesn't contain one.
    /// Phase 27 audit fix — anchor to non-[a-p] boundaries so an
    /// accidental 36-char run of [a-p] in some other path segment
    /// doesn't get spuriously matched as the first 32 chars.</summary>
    public static string? ExtractCwsId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        // Look for an exact 32-char [a-p] sequence that is NOT preceded
        // or followed by another [a-p] character. Word boundaries don't
        // work here because [a-p] is a strict subset of \w; we use
        // explicit lookaround instead.
        var match = Regex.Match(input,
            @"(?<![a-pA-P])[a-pA-P]{32}(?![a-pA-P])");
        if (!match.Success) return null;
        return match.Value.ToLowerInvariant();
    }
}

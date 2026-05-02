// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 27 — Extensions page VM. Lists the installed extension library
/// with search + enable toggle + uninstall. The "+ Install" toolbar
/// command opens <see cref="ExtensionInstallDialog"/> which handles
/// the four install paths (file / folder / store / paste URL).
/// </summary>
public sealed partial class ExtensionsViewModel : BaseViewModel
{
    private readonly IExtensionService _extensions;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ExtensionsViewModel> _log;

    public ExtensionsViewModel(
        IExtensionService extensions,
        IDialogService dialogs,
        ILogger<ExtensionsViewModel> log)
    {
        _extensions = extensions;
        _dialogs    = dialogs;
        _log        = log;

        // Phase 27 audit fix — search debounce. The TextBox binding
        // fires PropertyChanged on every keystroke; without this each
        // letter would round-trip the DB and re-build the row list.
        // 250ms window collapses bursts into a single reload.
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = ReloadAsync();
        };
    }

    private readonly DispatcherTimer _searchDebounce;

    public ObservableCollection<ExtensionRow> Items { get; } = new();

    [ObservableProperty] private string? _searchText;
    [ObservableProperty] private bool _isEmpty = true;

    public override async Task OnNavigatedToAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            Items.Clear();
            var rows = await _extensions.ListAsync(
                search: string.IsNullOrWhiteSpace(SearchText) ? null : SearchText);
            foreach (var r in rows) Items.Add(new ExtensionRow(r));
            IsEmpty = Items.Count == 0;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Extensions reload failed");
            await _dialogs.ConfirmAsync("Couldn't load extensions", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsBusy = false; }
    }

    partial void OnSearchTextChanged(string? value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var dlg = new ExtensionInstallDialog(_extensions) { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.Installed is not null)
        {
            await _dialogs.ConfirmAsync(
                "Extension installed",
                $"'{dlg.Installed.Name}' v{dlg.Installed.Version} added to the library.",
                "OK", ConfirmSeverity.Success);
            await ReloadAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleEnabledAsync(ExtensionRow? row)
    {
        if (row is null) return;
        try
        {
            await _extensions.SetGlobalEnabledAsync(row.Item.Id, !row.Item.Enabled);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Couldn't toggle extension");
            await _dialogs.ConfirmAsync("Toggle failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(ExtensionRow? row)
    {
        if (row is null) return;
        var ok = await _dialogs.ConfirmAsync(
            "Remove extension",
            $"Permanently remove '{row.Item.Name}'? Per-profile overrides will be cleared.",
            "Remove", ConfirmSeverity.Warning);
        if (!ok) return;
        try
        {
            await _extensions.UninstallAsync(row.Item.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Uninstall failed");
            await _dialogs.ConfirmAsync("Uninstall failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }
}

/// <summary>Row VM around <see cref="ExtensionItem"/>. Keeps the model
/// immutable and lets DataTemplate bindings resolve directly to the
/// underlying record's fields.</summary>
public sealed partial class ExtensionRow : ObservableObject
{
    public ExtensionItem Item { get; }
    public ExtensionRow(ExtensionItem item) { Item = item; }
    public string Name        => Item.Name;
    public string Version     => "v" + Item.Version;
    public string? Description => Item.Description;
    public string Source      => Item.Source;
    public string SourceLabel => Item.Source switch
    {
        "store"  => "🏪  store",
        "zip"    => "🗜  zip",
        "crx"    => "📦  crx",
        "folder" => "📁  folder",
        _        => Item.Source,
    };

    /// <summary>Absolute path to the extension's icon file. Returns
    /// null when the manifest didn't declare any (the view falls back
    /// to the 🧩 placeholder glyph).</summary>
    public string? IconPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Item.IconPath)) return null;
            try { return File.Exists(Item.IconPath) ? Item.IconPath : null; }
            catch { return null; }
        }
    }

    /// <summary>True when no icon file is on disk — drives the
    /// fallback puzzle-piece glyph in the card.</summary>
    public bool HasIcon => IconPath is not null;

    public bool Enabled     => Item.Enabled;

    /// <summary>Stable left-edge accent colour for the card. Hashes
    /// the extension id (32 [a-p] chars) to one of the 8 theme hues
    /// so the same extension keeps the same colour across reloads
    /// while the wall-of-cards reads as a colour map at a glance.</summary>
    public string AccentBrushKey
    {
        get
        {
            string[] palette =
            {
                "HueBlue", "HueGreen", "HueAmber", "HueOrange",
                "HuePink", "HueViolet", "HueTeal", "HueIndigo",
            };
            var src = Item.ExtId ?? Item.Name ?? "";
            int h = 0;
            foreach (var c in src) h = unchecked(h * 31 + c);
            int i = ((h % palette.Length) + palette.Length) % palette.Length;
            return palette[i];
        }
    }
}

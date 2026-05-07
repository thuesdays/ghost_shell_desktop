// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Navigation;
using GhostShell.Core.Services;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Top-level VM bound to MainWindow.xaml. Owns the sidebar items,
/// the active page, and the title-bar drag region.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _nav;
    private readonly IUpdateService _updateService;

    /// <summary>Phase 29 — bell-button + drawer VM, exposed so the
    /// title bar binds the badge count and the drawer's IsOpen flag
    /// to it. Wired by DI.</summary>
    public NotificationsViewModel Notifications { get; }

    public MainViewModel(INavigationService nav, NotificationsViewModel notifications, IUpdateService updateService)
    {
        _nav = nav;
        _updateService = updateService;
        Notifications = notifications;

        // Phase 71 — observe update pending state so the UI can show
        // "Update preparing — scheduler paused" banner.
        _updateService.UpdatePendingChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsUpdatePending));
        };
        // Set initial state
        OnPropertyChanged(nameof(IsUpdatePending));

        // Build the nav list FIRST — the CurrentChanged handler walks
        // it to update the highlight, and subscribing before assignment
        // would let the compiler complain about NavItems being null
        // inside the lambda's capture.
        NavItems    = BuildNavItems();
        FooterItems = BuildFooterItems();

        _nav.CurrentChanged += (_, _) =>
        {
            Current    = _nav.Current;
            CurrentKey = _nav.CurrentKey;
            // Phase 71q — walk both NavItems AND FooterItems (Settings
            // lives in the footer collection now). Also descend into
            // any group's Children so sub-items like Scheduler/Runs/
            // Queue under Monitoring light up correctly.
            UpdateSelection(NavItems);
            UpdateSelection(FooterItems);
            // Phase 71v — refresh the back-chip's visibility +
            // label whenever Current changes (the Back-stack only
            // mutates inside NavigateTo / GoBack, both of which
            // raise CurrentChanged).
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(BackTooltip));
        };

        // Default landing page
        _nav.NavigateTo("overview");
    }

    [ObservableProperty]
    private BaseViewModel? _current;

    [ObservableProperty]
    private string? _currentKey;

    /// <summary>Phase 71 — true when an update is preparing (download +
    /// extract + waiting for active runs to drain). Used to show a banner
    /// "Update preparing — scheduler paused".</summary>
    public bool IsUpdatePending => _updateService.IsUpdatePending;

    /// <summary>Phase 71s — bubbles the active page's IsBusy flag up to
    /// the window so the LoadingOverlay (and BlurEffect on the body)
    /// can be hosted at MainWindow level. That way the dim+blur covers
    /// both the sidebar AND the content while a page hydrates, instead
    /// of leaving the sidebar weirdly crisp on top of a blurred page.</summary>
    public bool IsBusy => Current?.IsBusy ?? false;

    /// <summary>Phase 71s — pivot the per-page PropertyChanged subscription
    /// whenever Current swaps. The previous page (if any) had its handler
    /// detached so we don't leak; the new page gets a fresh hook so
    /// IsBusy on it propagates to MainViewModel.IsBusy.</summary>
    partial void OnCurrentChanged(BaseViewModel? oldValue, BaseViewModel? newValue)
    {
        if (oldValue is INotifyPropertyChanged prev)
            prev.PropertyChanged -= OnCurrentPagePropertyChanged;
        if (newValue is INotifyPropertyChanged next)
            next.PropertyChanged += OnCurrentPagePropertyChanged;

        // The new page may have IsBusy=true already (e.g. its
        // OnNavigatedToAsync set the flag synchronously before the
        // subscription was wired). Push one notify so the overlay
        // shows immediately on the very first navigation tick.
        OnPropertyChanged(nameof(IsBusy));
    }

    private void OnCurrentPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BaseViewModel.IsBusy))
            OnPropertyChanged(nameof(IsBusy));
    }

    public ObservableCollection<SidebarRow> NavItems { get; }

    /// <summary>Phase 71q — items pinned to the bottom of the
    /// sidebar (above the Status footer). Currently just Settings;
    /// follow the same template / selection logic as NavItems but
    /// rendered into a separate DockPanel.Dock=Bottom region in
    /// MainWindow.xaml so they stay visible regardless of scroll.</summary>
    public ObservableCollection<SidebarRow> FooterItems { get; }

    /// <summary>Phase 71q — sync IsSelected across a sidebar
    /// collection AND any group rows' Children. Run for both
    /// NavItems and FooterItems on every CurrentChanged.</summary>
    private void UpdateSelection(ObservableCollection<SidebarRow> rows)
    {
        foreach (var it in rows)
        {
            if (it.PageKey is not null)
            {
                it.IsSelected = string.Equals(
                    it.PageKey, CurrentKey, StringComparison.OrdinalIgnoreCase);
            }
            if (it.Children is not null)
            {
                foreach (var child in it.Children)
                {
                    if (child.PageKey is null) continue;
                    child.IsSelected = string.Equals(
                        child.PageKey, CurrentKey, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [RelayCommand]
    private void Navigate(string? pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey)) return;
        // Sidebar / footer click = root nav. Pass pushHistory=false
        // so the back-stack gets cleared (sidebar should never leave
        // a Back chip — it's the user's explicit "take me here"
        // gesture, not a deep link).
        _nav.NavigateTo(pageKey, pushHistory: false);
    }

    /// <summary>Phase 71v — bound to the Back chip in the title-row of
    /// MainWindow. True when the user reached the current page via
    /// an in-page deep link (Overview tile, "View all runs", etc.).</summary>
    public bool CanGoBack => _nav.CanGoBack;

    /// <summary>Phase 71v — humanised tooltip for the Back chip so the
    /// user sees where it'll take them ("Back to Overview") instead
    /// of just an arrow.</summary>
    public string BackTooltip
    {
        get
        {
            // No prior page → no chip is shown anyway, but keep
            // a sensible fallback in case the binding is read.
            return _nav.CanGoBack ? "Back" : string.Empty;
        }
    }

    [RelayCommand]
    private void GoBack() => _nav.GoBack();

    // ─── Segoe Fluent Icons code points ────────────────────────────
    // Built from numeric values so the source stays pure ASCII. These
    // are present in both Segoe Fluent Icons (Win11) and Segoe MDL2
    // Assets (Win10), so the icon TextBlock can use either as a font
    // family and render the same glyph.
    private static string Glyph(int codepoint) => char.ConvertFromUtf32(codepoint);

    /// <summary>Resolve a theme-brush by key. Used during startup
    /// to colour each sidebar icon with its per-page hue. Falls
    /// back to white if the resource isn't loaded yet (which can
    /// happen if the call runs before App.xaml finishes parsing —
    /// in practice we're always called after).</summary>
    private static Brush Hue(string key)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return Brushes.White;
    }

    private static ObservableCollection<SidebarRow> BuildNavItems() => new()
    {
        SidebarRow.Section("Workspace"),
        new SidebarRow { PageKey = "overview",  Label = "Overview",  Icon = Glyph(0xE80F), IconBrush = Hue("HueBlue")   }, // Home
        new SidebarRow { PageKey = "profiles",  Label = "Profiles",  Icon = Glyph(0xE77B), IconBrush = Hue("HueGreen")  }, // Contact
        new SidebarRow { PageKey = "groups",    Label = "Groups",    Icon = Glyph(0xE902), IconBrush = Hue("HueIndigo") }, // FolderHorizontal

        // Scripts is automation territory — pulled out of Workspace
        // so the sidebar groups by what each section does (Workspace
        // = entities, Automation = behaviour, Identity = stealth,
        // Monitoring = observation).
        SidebarRow.Section("Automation"),
        new SidebarRow { PageKey = "scripts",   Label = "Scripts",   Icon = Glyph(0xE7C3), IconBrush = Hue("HueAmber")  }, // Page

        SidebarRow.Section("Identity"),
        new SidebarRow { PageKey = "proxy",       Label = "Proxy",        Icon = Glyph(0xE968), IconBrush = Hue("HueTeal")   }, // Globe
        new SidebarRow { PageKey = "fingerprint", Label = "Fingerprint",  Icon = Glyph(0xE8FD), IconBrush = Hue("HueGreen")  }, // Identity
        new SidebarRow { PageKey = "sessions",    Label = "Sessions",     Icon = Glyph(0xE81C), IconBrush = Hue("HueViolet") }, // Library
        new SidebarRow { PageKey = "packs",       Label = "Cookie packs", Icon = Glyph(0xE7B8), IconBrush = Hue("HuePink")   }, // Package
        // Phase 25 — credential vault. Lives in Identity because it's
        // identity-adjacent (logins for sites the profile pretends to be).
        new SidebarRow { PageKey = "vault",       Label = "Vault",        Icon = Glyph(0xE192), IconBrush = Hue("HueAmber")  }, // Lock
        // Phase 27 — browser extensions. Sits in Identity because the
        // set of extensions a profile loads is part of how that profile
        // looks to sites (uBlock blocking trackers, MetaMask leaking
        // wallet-specific JS APIs, etc.).
        new SidebarRow { PageKey = "extensions",  Label = "Extensions",   Icon = Glyph(0xECAA), IconBrush = Hue("HuePink")   }, // Puzzle

        // Phase 71q — Competitors and Monitoring (group) merged into
        // Identity for a tighter, single-section feel. The legacy
        // "Advertisement" + "Monitoring" headers are gone — fewer
        // dividers visually, all secondary surfaces live under one
        // umbrella. Settings moves out entirely (now in FooterItems
        // pinned to the sidebar bottom).
        new SidebarRow { PageKey = "competitors", Label = "Competitors", Icon = Glyph(0xE9F9), IconBrush = Hue("HueOrange") }, // BankBuilding

        new SidebarRow
        {
            PageKey   = null,                    // group has no page of its own
            Label     = "Monitoring",
            Icon      = Glyph(0xE9D9),           // BarChart4Legend
            IconBrush = Hue("HueBlue"),
            Children  = new[]
            {
                new SidebarRow { PageKey = "scheduler", Label = "Scheduler", Icon = Glyph(0xE787), IconBrush = Hue("HueAmber")  }, // Calendar
                new SidebarRow { PageKey = "runs",      Label = "Runs",      Icon = Glyph(0xE823), IconBrush = Hue("HueOrange") }, // History
                new SidebarRow { PageKey = "queue",     Label = "Queue",     Icon = Glyph(0xE71D), IconBrush = Hue("HueTeal")   }, // Boards
                new SidebarRow { PageKey = "traffic",   Label = "Traffic",   Icon = Glyph(0xE9D9), IconBrush = Hue("HueBlue")   }, // BarChart4Legend
                new SidebarRow { PageKey = "logs",      Label = "Logs",      Icon = Glyph(0xE7C3), IconBrush = Hue("HueAmber")  }, // Page
            },
        },
    };

    /// <summary>Phase 71q — items pinned to the sidebar bottom
    /// (above the Status footer). Settings is the only one for
    /// now; future global utilities (Help, About, Sign-out) would
    /// land here too.</summary>
    private static ObservableCollection<SidebarRow> BuildFooterItems() => new()
    {
        new SidebarRow { PageKey = "settings", Label = "Settings", Icon = Glyph(0xE713), IconBrush = Hue("HueSlate") },
    };
}

/// <summary>
/// Sidebar row data — flat shape so the ItemsControl template stays
/// simple. Either a section label (no PageKey, IsSection true) or
/// an actual nav item.
/// </summary>
public sealed partial class SidebarRow : ObservableObject
{
    public string? PageKey { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Icon  { get; init; } = string.Empty;
    public bool   IsSection { get; init; }

    /// <summary>
    /// Per-page icon tint. Mirrors the legacy web's hue palette so
    /// the sidebar reads as a colour map at a glance (Overview blue,
    /// Profiles green, Runs orange, Proxy teal, etc.). Bound to the
    /// icon TextBlock's Foreground in the sidebar template.
    /// </summary>
    public Brush IconBrush { get; init; } = Brushes.White;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Phase 71m — sub-items for hover-flyout groups. When non-null
    /// and non-empty, this row renders via the group template
    /// (icon + popup with children) instead of the regular nav-item
    /// template. Used to fold the Monitoring section into a single
    /// icon entry — Scheduler/Runs/Queue/Traffic/Logs hide inside.
    /// </summary>
    public IReadOnlyList<SidebarRow>? Children { get; init; }

    public bool HasChildren => Children is { Count: > 0 };

    /// <summary>
    /// Phase 71m — popup visibility for group rows. Set true on
    /// MouseEnter of the group's host Grid (with a small grace
    /// timer on leave so the user can shuffle the cursor across
    /// the gap into the popup body without losing it).
    /// </summary>
    [ObservableProperty]
    private bool _isExpanded;

    public static SidebarRow Section(string label) => new()
    {
        Label = label, IsSection = true,
    };
}

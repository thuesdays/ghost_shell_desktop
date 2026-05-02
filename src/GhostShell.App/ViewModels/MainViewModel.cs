// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Navigation;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Top-level VM bound to MainWindow.xaml. Owns the sidebar items,
/// the active page, and the title-bar drag region.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly INavigationService _nav;

    /// <summary>Phase 29 — bell-button + drawer VM, exposed so the
    /// title bar binds the badge count and the drawer's IsOpen flag
    /// to it. Wired by DI.</summary>
    public NotificationsViewModel Notifications { get; }

    public MainViewModel(INavigationService nav, NotificationsViewModel notifications)
    {
        _nav = nav;
        Notifications = notifications;

        // Build the nav list FIRST — the CurrentChanged handler walks
        // it to update the highlight, and subscribing before assignment
        // would let the compiler complain about NavItems being null
        // inside the lambda's capture.
        NavItems = BuildNavItems();

        _nav.CurrentChanged += (_, _) =>
        {
            Current    = _nav.Current;
            CurrentKey = _nav.CurrentKey;
            foreach (var it in NavItems)
            {
                if (it.PageKey is null) continue;
                it.IsSelected = string.Equals(
                    it.PageKey, CurrentKey, StringComparison.OrdinalIgnoreCase);
            }
        };

        // Default landing page
        _nav.NavigateTo("overview");
    }

    [ObservableProperty]
    private BaseViewModel? _current;

    [ObservableProperty]
    private string? _currentKey;

    public ObservableCollection<SidebarRow> NavItems { get; }

    [RelayCommand]
    private void Navigate(string? pageKey)
    {
        if (string.IsNullOrWhiteSpace(pageKey)) return;
        _nav.NavigateTo(pageKey);
    }

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

        // Phase 34 — Advertisement section. Competitors is a pure
        // observation surface for ads we've seen across runs.
        //
        // Domains used to live here too but was demoted off the
        // sidebar — the lists are configured via a "Domain lists"
        // toolbar button on the Scripts page (where they're
        // actually consumed). The page+VM+route stay registered so
        // that button can navigate to "domains" the same as before.
        SidebarRow.Section("Advertisement"),
        new SidebarRow { PageKey = "competitors", Label = "Competitors", Icon = Glyph(0xE9F9), IconBrush = Hue("HueOrange") }, // BankBuilding

        // Scheduler + Runs were originally up under Workspace because
        // they're "things you start work with". Demoted into Monitoring
        // because in practice they're observation surfaces — you go to
        // Scheduler to see what's queued up, you go to Runs to see what
        // happened. The only entry point users actually invoke from
        // these pages is "Run now", and that's also reachable from
        // Profiles/Groups directly.
        SidebarRow.Section("Monitoring"),
        new SidebarRow { PageKey = "scheduler", Label = "Scheduler", Icon = Glyph(0xE787), IconBrush = Hue("HueAmber")  }, // Calendar
        new SidebarRow { PageKey = "runs",      Label = "Runs",      Icon = Glyph(0xE823), IconBrush = Hue("HueOrange") }, // History
        new SidebarRow { PageKey = "traffic",   Label = "Traffic",   Icon = Glyph(0xEA0B), IconBrush = Hue("HueBlue")   }, // BarChart
        new SidebarRow { PageKey = "logs",     Label = "Logs",      Icon = Glyph(0xE7C3), IconBrush = Hue("HueAmber")  }, // Page

        SidebarRow.Section(""),
        new SidebarRow { PageKey = "settings", Label = "Settings",  Icon = Glyph(0xE713), IconBrush = Hue("HueSlate")  }, // Settings
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

    public static SidebarRow Section(string label) => new()
    {
        Label = label, IsSection = true,
    };
}

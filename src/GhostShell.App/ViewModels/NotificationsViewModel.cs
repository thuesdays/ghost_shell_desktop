// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Navigation;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Phase 29 — bell-button + drawer VM. Bound to the title-bar bell
/// (badge count) AND to the slide-out drawer (item list). Subscribes
/// to <see cref="INotificationService.Changed"/> + a 30-second timer
/// so out-of-process triggers (run failures, scheduler hiccups) bubble
/// up without the user clicking refresh.
/// </summary>
public sealed partial class NotificationsViewModel : ObservableObject
{
    private readonly INotificationService _notifications;
    private readonly INavigationService _nav;
    private readonly ILogger<NotificationsViewModel> _log;
    private readonly DispatcherTimer _poll;

    public NotificationsViewModel(
        INotificationService notifications,
        INavigationService nav,
        ILogger<NotificationsViewModel> log)
    {
        _notifications = notifications;
        _nav           = nav;
        _log           = log;
        _notifications.Changed += (_, _) => MarshalRefresh();

        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _poll.Tick += async (_, _) => await RefreshAsync();
        _poll.Start();

        // First load is fired-and-forgotten — the badge stays at 0
        // until it completes. Acceptable; the alternative is starting
        // up with a "loading" placeholder.
        _ = RefreshAsync();
    }

    public ObservableCollection<Notification> Items { get; } = new();

    /// <summary>True when the drawer is open. Bound TwoWay to the
    /// drawer's IsOpen flag so closing via Escape / backdrop-click
    /// flips this back to false.</summary>
    [ObservableProperty] private bool _isDrawerOpen;

    /// <summary>Badge count — currently active (undismissed) entries.</summary>
    [ObservableProperty] private int _activeCount;

    /// <summary>Highest severity among active entries — drives badge tint.</summary>
    [ObservableProperty] private string _badgeSeverity = NotificationSeverity.Info;

    /// <summary>Pre-formatted text for the badge ("99+" past 99).</summary>
    public string BadgeText => ActiveCount > 99 ? "99+" : ActiveCount.ToString();
    public bool HasActive => ActiveCount > 0;

    // Single Changed-callback that fans out the dependent properties.
    // Source generator only allows one overload per property; using
    // both 1-arg and 2-arg versions doesn't compile.
    partial void OnActiveCountChanged(int value)
    {
        OnPropertyChanged(nameof(BadgeText));
        OnPropertyChanged(nameof(HasActive));
    }

    [RelayCommand]
    public void OpenDrawer()
    {
        IsDrawerOpen = true;
        _ = RefreshAsync();
    }

    [RelayCommand]
    private void CloseDrawer() => IsDrawerOpen = false;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var rows = await _notifications.ListActiveAsync(100);
            Items.Clear();
            foreach (var n in rows) Items.Add(n);
            ActiveCount = rows.Count;
            // Severity tint = highest severity present.
            var top = NotificationSeverity.Info;
            int topRank = NotificationSeverity.Rank(top);
            foreach (var n in rows)
            {
                var r = NotificationSeverity.Rank(n.Severity);
                if (r < topRank) { topRank = r; top = n.Severity; }
            }
            BadgeSeverity = rows.Count == 0 ? NotificationSeverity.Info : top;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Notifications refresh failed");
        }
    }

    [RelayCommand]
    private async Task DismissAsync(Notification? n)
    {
        if (n is null) return;
        try { await _notifications.DismissAsync(n.Id); }
        catch (Exception ex) { _log.LogWarning(ex, "Dismiss failed"); }
    }

    [RelayCommand]
    private async Task DismissAllAsync()
    {
        try { await _notifications.DismissAllAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "DismissAll failed"); }
    }

    /// <summary>Click-target verb dispatcher. Maps the persisted
    /// <see cref="Notification.Action"/> string to a navigation /
    /// shell-execute call.</summary>
    [RelayCommand]
    private async Task ActivateAsync(Notification? n)
    {
        if (n is null) return;
        // Always dismiss when the user activates — once they've seen
        // it and acted, the badge shouldn't keep nagging.
        await DismissAsync(n);
        IsDrawerOpen = false;
        try
        {
            switch (n.Action)
            {
                case "open_profile":   _nav.NavigateTo("profiles");  break;
                case "open_proxy":     _nav.NavigateTo("proxy");     break;
                case "open_runs":      _nav.NavigateTo("runs");      break;
                case "open_scheduler": _nav.NavigateTo("scheduler"); break;
                case "open_traffic":   _nav.NavigateTo("traffic");   break;
                case "open_logs":      _nav.NavigateTo("logs");      break;
                case "url":
                    if (!string.IsNullOrWhiteSpace(n.ActionArg))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = n.ActionArg,
                            UseShellExecute = true,
                        });
                    }
                    break;
                default:
                    // Unknown verb — open the home page so the user
                    // can manually find the relevant section.
                    _nav.NavigateTo("overview");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Notification action '{Action}' failed", n.Action);
        }
    }

    private void MarshalRefresh()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null) { _ = RefreshAsync(); return; }
        disp.BeginInvoke(new Action(async () => await RefreshAsync()));
    }
}

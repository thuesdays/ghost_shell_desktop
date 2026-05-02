// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using GhostShell.App.Navigation;
using GhostShell.Core.Common;
using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Tray;

/// <summary>
/// Tray icon host. Owns the NotifyIcon lifecycle, context menu,
/// tooltip refresh (active-run count), and balloon notifications.
/// Runs as a singleton IHostedService — stays alive when the main
/// window is hidden.
/// </summary>
public sealed class TrayIconHost : IHostedService, IDisposable
{
    private readonly IRunService _runService;
    private readonly IProfileService _profileService;
    private readonly IProfileRunner _profileRunner;
    private readonly INavigationService _navigationService;
    private readonly ILogger<TrayIconHost> _logger;

    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _contextMenu;
    private DispatcherTimer? _refreshTimer;
    private System.Drawing.Icon? _iconCached;
    private bool _disposed;
    private int _pollInFlight;

    // First-time minimize balloon flag — kept here for future
    // wiring through ISettingsService (so the "still running"
    // balloon is shown once per machine, not once per app session).
    // The MainWindow currently uses an in-memory bool; this constant
    // reserves the settings key so we don't drift naming later.
#pragma warning disable CS0414  // assigned-but-never-used: intentional placeholder
    private static readonly string BalloonShownKey = "tray.first_minimize_balloon_shown";
#pragma warning restore CS0414

    public TrayIconHost(
        IRunService runService,
        IProfileService profileService,
        IProfileRunner profileRunner,
        INavigationService navigationService,
        ILogger<TrayIconHost> logger)
    {
        _runService = runService;
        _profileService = profileService;
        _profileRunner = profileRunner;
        _navigationService = navigationService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _logger.LogInformation("TrayIconHost starting");

        try
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                CreateAndShowTrayIcon();
                StartRefreshTimer();
            }, DispatcherPriority.Normal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize tray icon");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _logger.LogInformation("TrayIconHost stopping");

        // Mark disposed first so any in-flight tick callback bails
        // before touching state we're about to tear down.
        _disposed = true;

        if (_refreshTimer != null)
        {
            try { _refreshTimer.Stop(); } catch { /* timer may be cross-thread */ }
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer = null;
        }

        // Phase 38 fix — dispose tray icon defensively. By the time
        // StopAsync runs, WPF's Application.Current may already be
        // null (host StopAsync racing the WPF teardown), so a naive
        // Application.Current.Dispatcher.InvokeAsync NREs. Snapshot
        // the dispatcher first; if missing, dispose synchronously
        // (NotifyIcon.Dispose is technically thread-affine but the
        // worst case here is a benign exception during process exit
        // which we swallow).
        var trayIcon = _trayIcon;
        _trayIcon = null;
        if (trayIcon != null)
        {
            var app = Application.Current;
            var dispatcher = app?.Dispatcher;
            if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            {
                try
                {
                    await dispatcher.InvokeAsync(() =>
                    {
                        try { trayIcon.Visible = false; } catch { }
                        try { trayIcon.Dispose(); } catch { }
                    }, DispatcherPriority.Normal);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Tray-icon dispose dispatcher invoke failed — falling back to direct dispose");
                    try { trayIcon.Visible = false; } catch { }
                    try { trayIcon.Dispose(); } catch { }
                }
            }
            else
            {
                // No dispatcher / already shutting down — best-effort direct.
                try { trayIcon.Visible = false; } catch { }
                try { trayIcon.Dispose(); } catch { }
            }
        }

        try { _iconCached?.Dispose(); } catch { }
        _iconCached = null;
    }

    private void CreateAndShowTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "Ghost Shell — idle",
        };

        // Load icon from pack URI
        try
        {
            var resourceUri = new Uri("pack://application:,,,/Assets/AppIcon.ico");
            var resourceStream = System.Windows.Application.GetResourceStream(resourceUri);
            if (resourceStream?.Stream is { } stream)
            {
                var newIcon = new System.Drawing.Icon(stream);
                _iconCached = newIcon;
                _trayIcon.Icon = newIcon;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load AppIcon.ico; tray icon may be blank");
        }

        // Build context menu
        _contextMenu = new Forms.ContextMenuStrip();

        // 1. Open Ghost Shell (default/bold)
        var openItem = new Forms.ToolStripMenuItem("Open Ghost Shell");
        openItem.Click += (_, _) => ShowAndActivateMainWindow();
        _contextMenu.Items.Add(openItem);

        // 2. Hide window
        var hideItem = new Forms.ToolStripMenuItem("Hide window");
        hideItem.Click += (_, _) => HideMainWindow();
        _contextMenu.Items.Add(hideItem);

        // Separator
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());

        // 3. Run profile (submenu)
        var runProfileItem = new Forms.ToolStripMenuItem("Run profile");
        runProfileItem.DropDownOpening += async (_, _) => await PopulateProfilesSubmenu(runProfileItem);
        _contextMenu.Items.Add(runProfileItem);

        // 4. Stop all running
        var stopAllItem = new Forms.ToolStripMenuItem("Stop all running");
        stopAllItem.Click += (_, _) => _ = StopAllRunningWithErrorHandling();
        _contextMenu.Items.Add(stopAllItem);

        // Separator
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());

        // 5. Open logs folder
        var logsItem = new Forms.ToolStripMenuItem("Open logs folder");
        logsItem.Click += (_, _) => OpenLogsFolder();
        _contextMenu.Items.Add(logsItem);

        // 6. Settings
        var settingsItem = new Forms.ToolStripMenuItem("Settings");
        settingsItem.Click += (_, _) => NavigateToSettings();
        _contextMenu.Items.Add(settingsItem);

        // 7. Notifications
        var notificationsItem = new Forms.ToolStripMenuItem("Notifications");
        notificationsItem.Click += (_, _) => NavigateToNotifications();
        _contextMenu.Items.Add(notificationsItem);

        // Separator
        _contextMenu.Items.Add(new Forms.ToolStripSeparator());

        // 8. About
        var aboutItem = new Forms.ToolStripMenuItem("About");
        aboutItem.Click += (_, _) => ShowAbout();
        _contextMenu.Items.Add(aboutItem);

        // 9. Quit Ghost Shell
        var quitItem = new Forms.ToolStripMenuItem("Quit Ghost Shell");
        quitItem.Click += (_, _) =>
        {
            App.AllowingShutdown = true;
            foreach (Window w in Application.Current.Windows)
            {
                if (w is MainWindow mw) mw.AllowClose();
            }
            Application.Current.Shutdown(0);
        };
        _contextMenu.Items.Add(quitItem);

        _trayIcon.ContextMenuStrip = _contextMenu;

        // Double-click and single-click to show window
        _trayIcon.DoubleClick += (_, _) => ShowAndActivateMainWindow();
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowAndActivateMainWindow();
        };
    }

    private void StartRefreshTimer()
    {
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
        _logger.LogDebug("Tray refresh timer started (5s interval)");
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (_trayIcon == null) return;

        // Prevent concurrent polling tasks
        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
            return;

        try
        {
            // Count active runs on a background thread to avoid blocking the UI
            _ = Task.Run(async () =>
            {
                try
                {
                    var runs = await _runService.ListAsync(limit: 100, status: Core.Services.RunStatusFilter.Running);
                    var activeCount = runs.Count;

                    var lastFailed = await _runService.ListAsync(
                        limit: 1,
                        status: Core.Services.RunStatusFilter.Failed);

                    var lastFailedRecent = lastFailed.FirstOrDefault() is { FinishedAt: not null } run
                        && (DateTime.UtcNow - run.FinishedAt.Value).TotalSeconds < 60;

                    // Clamp tooltip to 63 chars (Windows limit) with ellipsis if needed
                    var newTooltip = activeCount > 0
                        ? $"Ghost Shell — {activeCount} run(s) active"
                        : lastFailedRecent
                            ? "Ghost Shell — last run failed"
                            : "Ghost Shell — idle";

                    if (newTooltip.Length > 63)
                        newTooltip = newTooltip.Substring(0, 60) + "…";

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_trayIcon != null && !_disposed)
                            _trayIcon.Text = newTooltip;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh tray tooltip");
                }
                finally
                {
                    Interlocked.Exchange(ref _pollInFlight, 0);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray refresh timer exception");
            Interlocked.Exchange(ref _pollInFlight, 0);
        }
    }

    private async Task PopulateProfilesSubmenu(Forms.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();

        try
        {
            var profiles = await _profileService.ListAsync();
            var profileList = profiles.ToList();

            // Cap at 12 profiles, last is "More…" link
            if (profileList.Count > 12)
            {
                profileList = profileList.Take(11).ToList();

                var moreItem = new Forms.ToolStripMenuItem("More…");
                moreItem.Click += (_, _) =>
                {
                    ShowAndActivateMainWindow();
                    _navigationService.NavigateTo("profiles");
                };
                parent.DropDownItems.Add(moreItem);
            }

            // Add profile items
            foreach (var profile in profileList)
            {
                var profileName = profile.Name;
                // Capture the Profile model — IProfileRunner.StartAsync takes
                // the full record (not a name string). Closing over `profile`
                // directly is cleaner than re-fetching by name on click.
                var capturedProfile = profile;
                // Escape & to && for Forms menu item text display
                var displayName = profileName.Replace("&", "&&");
                var item = new Forms.ToolStripMenuItem(displayName);
                item.Click += (_, _) => _ = StartProfileWithErrorHandling(capturedProfile, profileName);
                parent.DropDownItems.Add(item);
            }

            if (profileList.Count == 0)
            {
                var emptyItem = new Forms.ToolStripMenuItem("(no profiles)") { Enabled = false };
                parent.DropDownItems.Add(emptyItem);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to populate profiles submenu");
            var errorItem = new Forms.ToolStripMenuItem("(error loading)") { Enabled = false };
            parent.DropDownItems.Add(errorItem);
        }
    }

    private async Task StartProfileWithErrorHandling(GhostShell.Core.Models.Profile profile, string profileName)
    {
        try
        {
            await _profileRunner.StartAsync(profile);
            _logger.LogInformation("Started profile {ProfileName} from tray", profileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start profile {ProfileName}", profileName);
            ShowBalloon("Failed to start profile", $"Error: {ex.Message}", Forms.ToolTipIcon.Error);
        }
    }

    private async Task StopAllRunningWithErrorHandling()
    {
        try
        {
            var runs = await _runService.ListAsync(limit: 100, status: Core.Services.RunStatusFilter.Running);
            var stopTasks = runs.Select(r => _profileRunner.StopAsync(r.ProfileName));
            await Task.WhenAll(stopTasks);
            _logger.LogInformation("Stopped all running profiles from tray");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop all running profiles");
            ShowBalloon("Failed to stop profiles", $"Error: {ex.Message}", Forms.ToolTipIcon.Error);
        }
    }

    private void HideMainWindow()
    {
        if (Application.Current.MainWindow is { } w)
        {
            w.Visibility = Visibility.Collapsed;
            w.ShowInTaskbar = false;
        }
    }

    public static void ShowAndActivateMainWindow()
    {
        if (Application.Current.MainWindow is { } w)
        {
            if (w.Visibility != Visibility.Visible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.ShowInTaskbar = true;
            w.Activate();
            w.Topmost = true;
            w.Topmost = false;
            w.Focus();
        }
    }

    public void ShowBalloon(string title, string body, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info)
    {
        if (_trayIcon != null)
        {
            try
            {
                _trayIcon.ShowBalloonTip(5000, title, body, icon);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show balloon tip");
            }
        }
    }

    private void OpenLogsFolder()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = AppPaths.LogsDir,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open logs folder");
            ShowBalloon("Failed to open logs", $"Error: {ex.Message}", Forms.ToolTipIcon.Error);
        }
    }

    private void NavigateToSettings()
    {
        ShowAndActivateMainWindow();
        _navigationService.NavigateTo("settings");
    }

    private void NavigateToNotifications()
    {
        ShowAndActivateMainWindow();
        _navigationService.NavigateTo("overview");
    }

    private void ShowAbout()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(4) ?? "?.?.?.?";
        MessageBox.Show(
            $"Ghost Shell v{version}",
            "About Ghost Shell",
            MessageBoxButton.OK,
            MessageBoxImage.Information,
            MessageBoxResult.OK,
            MessageBoxOptions.DefaultDesktopOnly);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_refreshTimer != null)
        {
            _refreshTimer.Stop();
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer = null;
        }

        _contextMenu?.Dispose();
        _trayIcon?.Dispose();
        _iconCached?.Dispose();
    }
}

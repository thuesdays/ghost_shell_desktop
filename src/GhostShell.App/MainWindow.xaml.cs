// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GhostShell.App.Navigation;
using GhostShell.App.Tray;
using GhostShell.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.App;

public partial class MainWindow : Window
{
    // Segoe MDL2 Assets glyph code points (works on Win10 1809+ / Win11):
    //   0xE922 = ChromeMaximize
    //   0xE923 = ChromeRestore (shown while window is already maximized)
    private static readonly string GlyphMaximize = char.ConvertFromUtf32(0xE922);
    private static readonly string GlyphRestore  = char.ConvertFromUtf32(0xE923);

    // Phase 38: Track whether we're actually closing or just hiding.
    // Set by AllowClose() from the App's Shutdown path (the tray's
    // "Quit" item calls Application.Current.Shutdown which fires
    // OnExit which calls AllowClose on every Window). The user's
    // X / Alt+F4 hits OnClosing without this flag set, so the window
    // hides instead.
    private bool _reallyClose;
    private bool _firstMinimizeBalloonShown;

    /// <summary>Called by App.OnExit before the dispatcher shuts down so
    /// our OnClosing handler knows to actually let the window go.</summary>
    public void AllowClose()
    {
        _reallyClose = true;
    }

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnWindowStateChanged;
        Closing += OnClosing;

        // Phase 71jj — restore window geometry from settings BEFORE
        // the window is shown. Loaded fires too late (window has
        // already laid out at the XAML default 1280×820 and any
        // resize at this point causes a visible re-layout). Doing it
        // synchronously in the ctor means the first frame is already
        // at the user's saved size + position. Best-effort: any
        // failure (corrupt setting, off-screen coordinates from a
        // monitor that's been disconnected) falls back to defaults.
        TryRestoreGeometry();

        // Version label in the title bar — pulled from the running
        // assembly so the same VERSION file drives BOTH the .exe
        // metadata AND the on-screen pill. Always show the full
        // four-part dotted form so a small revision bump is visible
        // at a glance ("0.0.1.5" reads differently from "0.0.1.0").
        // Falls back to "?" when the assembly somehow has no
        // version stamped — easy to spot in screenshots.
        // Phase 37 audit fix #6: Force 4-part display via ToString(4).
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        try
        {
            VersionLabel.Text = v?.ToString(4) ?? "?";
        }
        catch (ArgumentException)
        {
            // Fallback if ToString(4) somehow fails (shouldn't happen)
            VersionLabel.Text = v?.ToString() ?? "?";
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Swap the inner TextBlock's text so the icon reflects state.
        if (FindName("MaxIcon") is TextBlock tb)
            tb.Text = WindowState == WindowState.Maximized
                ? GlyphRestore
                : GlyphMaximize;
    }

    /// <summary>
    /// Sidebar item clicked — pull the page key out of the button's
    /// Tag property and route through the nav service. Button.Click
    /// is the right event here (works inside a DataTemplate, unlike
    /// Border + MouseLeftButtonUp which the XAML compiler refuses to
    /// validate when nested inside a Style).
    /// </summary>
    private void OnNavItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el && el.Tag is string pageKey)
        {
            var app = (App)Application.Current;
            var nav = app.Host?.Services.GetRequiredService<INavigationService>();
            nav?.NavigateTo(pageKey);

            // Phase 71m — clicking a sub-item inside a group's popup
            // should close the popup so the user immediately sees the
            // navigated page. Walk up the visual tree from the
            // clicked Button to find the SidebarRow whose IsExpanded
            // owns the popup, and flip it false.
            CollapseAnyOpenGroupFlyout();
        }
    }

    // ─── Phase 71m — Monitoring (and any future) group flyout ────────
    //
    // The sidebar group template hosts a Popup with StaysOpen=True;
    // its IsOpen is bound OneWay to SidebarRow.IsExpanded. We manage
    // that flag from code-behind so we can cover the gap between
    // button and popup with a small grace timer, otherwise the popup
    // would close the instant the cursor crossed the seam.

    private System.Windows.Threading.DispatcherTimer? _groupCloseTimer;
    private ViewModels.SidebarRow? _hoveredGroup;

    private void OnGroupMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ViewModels.SidebarRow row)
        {
            // Cancel any pending close — the user came back.
            _groupCloseTimer?.Stop();
            _hoveredGroup = row;
            row.IsExpanded = true;
        }
    }

    private void OnGroupMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ViewModels.SidebarRow row)
        {
            ScheduleGroupClose(row);
        }
    }

    private void OnGroupPopupMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Cursor moved INTO the popup — cancel close.
        _groupCloseTimer?.Stop();
    }

    private void OnGroupPopupMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is ViewModels.SidebarRow row)
        {
            ScheduleGroupClose(row);
        }
    }

    private void ScheduleGroupClose(ViewModels.SidebarRow row)
    {
        _groupCloseTimer?.Stop();
        _groupCloseTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = System.TimeSpan.FromMilliseconds(220),
        };
        _groupCloseTimer.Tick += (_, __) =>
        {
            _groupCloseTimer?.Stop();
            // Only close if neither the host Grid nor the popup is
            // currently being hovered. The MouseEnter handlers above
            // cancel the timer when the cursor returns, so by the time
            // we get here it's been ~220 ms since both were idle.
            row.IsExpanded = false;
            if (_hoveredGroup == row) _hoveredGroup = null;
        };
        _groupCloseTimer.Start();
    }

    private void CollapseAnyOpenGroupFlyout()
    {
        if (_hoveredGroup is { } row)
        {
            row.IsExpanded = false;
            _hoveredGroup = null;
        }
        _groupCloseTimer?.Stop();
    }

    /// <summary>Phase 29 audit fix — when the user clicks the per-row
    /// dismiss "✕" inside the notifications drawer, the click would
    /// otherwise bubble up to the parent Border's MouseBinding and ALSO
    /// fire ActivateCommand (dismiss + navigate). Marking the routed
    /// event as Handled stops the bubble; the Button's own Command
    /// (DismissCommand) still runs because Button raises Click before
    /// we mark it.</summary>
    private void OnNotificationDismissClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>
    /// Phase 38: Intercept window close. If not an actual shutdown,
    /// hide instead and show a balloon tip the first time.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Phase 71jj — persist geometry on EVERY close, including the
        // hide-to-tray path. Otherwise the user resizes, hides via X,
        // quits via tray (which doesn't go through OnClosing again
        // because we already cancelled the first one), and the new
        // size is lost. Saving here covers both paths cleanly:
        //   • X / Alt+F4 → Closing fires, we save, then we Cancel.
        //   • Tray Quit  → Shutdown calls AllowClose then Close → we
        //                  save again on the unconditional close.
        // The double-save is cheap (4 sets on the SQLite settings
        // table) and avoids race conditions with separate hooks.
        TryPersistGeometry();

        if (_reallyClose || App.AllowingShutdown)
        {
            // Application.Current.Shutdown() was called — let the close proceed
            return;
        }

        // User clicked X or Alt+F4 — hide instead.
        //
        // Phase 69b — drop the ShowInTaskbar=false call entirely. Hide()
        // already removes the window from the taskbar (WPF semantics:
        // Visibility=Hidden hides from taskbar regardless of the
        // ShowInTaskbar flag). Toggling ShowInTaskbar forces WPF to
        // tear down and recreate the native HWND, which re-fires
        // Window.Loaded — the original cause of the splash flicker on
        // tray-minimize. Skipping the toggle removes the entire HWND-
        // recreation path.
        e.Cancel = true;
        Hide();

        // Show balloon tip first time only
        if (!_firstMinimizeBalloonShown)
        {
            _firstMinimizeBalloonShown = true;
            try
            {
                var tray = (Application.Current as App)?.Host?.Services
                    .GetRequiredService<TrayIconHost>();
                tray?.ShowBalloon(
                    "Ghost Shell is still running",
                    "Ghost Shell is running in the background. " +
                    "Right-click the tray icon to quit.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
            catch { /* best-effort */ }
        }
    }

    // ─── Phase 71jj — Window geometry persistence ────────────────────

    /// <summary>Hard floor for the saved size. If a corrupt setting
    /// somewhere reports 50×30 we don't want to render a postage
    /// stamp window the user can't even drag.</summary>
    private const double MinSavedWidth  = 800;
    private const double MinSavedHeight = 600;

    /// <summary>
    /// Read the saved geometry from settings and apply it. Runs
    /// synchronously off the SQLite-backed ISettingsService — the
    /// service's ctor already loads the table into an in-memory
    /// snapshot, so the per-key reads are dictionary lookups, not
    /// disk hits. Best-effort: a missing service / bad value /
    /// off-screen coordinate from a now-disconnected monitor all
    /// fall back to the XAML default size.
    /// </summary>
    private void TryRestoreGeometry()
    {
        try
        {
            var settings = (Application.Current as App)?.Host?.Services
                .GetService<ISettingsService>();
            if (settings is null) return;

            // Synchronous read via .Result — these calls are wrapped
            // around an in-memory cache, so blocking the UI thread
            // for the few microseconds it takes is fine. Doing a
            // proper async dance here would require the window to
            // start at the default size and re-layout once the await
            // resolves, defeating the point.
            var w = settings.GetDoubleAsync(SettingsKeys.UiWindowWidth).GetAwaiter().GetResult();
            var h = settings.GetDoubleAsync(SettingsKeys.UiWindowHeight).GetAwaiter().GetResult();
            var top  = settings.GetDoubleAsync(SettingsKeys.UiWindowTop).GetAwaiter().GetResult();
            var left = settings.GetDoubleAsync(SettingsKeys.UiWindowLeft).GetAwaiter().GetResult();
            var max  = settings.GetBoolAsync(SettingsKeys.UiWindowMaximized).GetAwaiter().GetResult();

            if (w is double ww && ww >= MinSavedWidth)   Width  = ww;
            if (h is double hh && hh >= MinSavedHeight)  Height = hh;

            // Only honour Top/Left if the resulting rect is at least
            // partially on a known screen. SystemParameters reports
            // the primary screen's working area, which is the right
            // bound in single-monitor cases. For multi-monitor we'd
            // need to query System.Windows.Forms.Screen.AllScreens —
            // skip that for now and trust the user's last-good
            // position; if it's off-screen the WindowStartupLocation
            // fallback hands the OS a sane spot anyway.
            if (left is double ll && top is double tt
                && IsOnScreen(ll, tt, Width, Height))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = ll;
                Top  = tt;
            }

            // Apply maximized last so the size+position stamp the
            // restore-bounds, not the maximized bounds.
            if (max == true)
                WindowState = WindowState.Maximized;
        }
        catch
        {
            // Settings unavailable / DB locked / type mismatch — fall
            // back to the XAML defaults. Never let a corrupt setting
            // crash the app at startup.
        }
    }

    /// <summary>
    /// Persist current geometry. Called from OnClosing; uses the
    /// RestoreBounds when the window is currently maximized so we
    /// remember the size the user dragged it to before maximizing.
    /// Fire-and-forget async writes — by the time Application.Exit
    /// reaches us the in-memory cache has been flushed (the
    /// ISettingsService impl writes through to SQLite eagerly).
    /// </summary>
    private void TryPersistGeometry()
    {
        try
        {
            var settings = (Application.Current as App)?.Host?.Services
                .GetService<ISettingsService>();
            if (settings is null) return;

            // RestoreBounds gives us the un-maximized rect when
            // WindowState=Maximized; for Normal it's identical to
            // Width/Height/Top/Left. Either way it's the right
            // value to persist.
            var bounds = WindowState == WindowState.Maximized && !RestoreBounds.IsEmpty
                ? RestoreBounds
                : new Rect(Left, Top, Width, Height);

            // GetAwaiter().GetResult() — same justification as
            // restore: the settings service is in-memory cached.
            settings.SetDoubleAsync(SettingsKeys.UiWindowWidth,  bounds.Width).GetAwaiter().GetResult();
            settings.SetDoubleAsync(SettingsKeys.UiWindowHeight, bounds.Height).GetAwaiter().GetResult();
            settings.SetDoubleAsync(SettingsKeys.UiWindowLeft,   bounds.Left).GetAwaiter().GetResult();
            settings.SetDoubleAsync(SettingsKeys.UiWindowTop,    bounds.Top).GetAwaiter().GetResult();
            settings.SetBoolAsync(SettingsKeys.UiWindowMaximized,
                WindowState == WindowState.Maximized).GetAwaiter().GetResult();
        }
        catch
        {
            // Best-effort — never block shutdown for a settings write.
        }
    }

    /// <summary>
    /// Check whether (left, top, w, h) intersects the primary screen's
    /// virtual bounds. Cheap rough cut: if the saved rect is entirely
    /// off-screen (user moved the window to a monitor that's now
    /// disconnected) we fall back to default centering. Only checks
    /// the primary screen — multi-monitor edge cases will land on
    /// primary, which is a sane recovery.
    /// </summary>
    private static bool IsOnScreen(double left, double top, double w, double h)
    {
        // Use the system's virtual screen bounds — covers all
        // attached monitors. Negative origins are valid (monitors
        // arranged to the left of primary).
        var virtualLeft   = SystemParameters.VirtualScreenLeft;
        var virtualTop    = SystemParameters.VirtualScreenTop;
        var virtualRight  = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop  + SystemParameters.VirtualScreenHeight;

        var rectRight  = left + w;
        var rectBottom = top  + h;

        // Require at least 100×100px of overlap so the title bar is
        // grabbable. Anything less probably means the window's almost
        // entirely off the edge.
        var overlapW = System.Math.Max(0,
            System.Math.Min(rectRight, virtualRight) - System.Math.Max(left, virtualLeft));
        var overlapH = System.Math.Max(0,
            System.Math.Min(rectBottom, virtualBottom) - System.Math.Max(top, virtualTop));
        return overlapW >= 100 && overlapH >= 100;
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using GhostShell.App.Navigation;
using GhostShell.App.Tray;
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
        }
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
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using GhostShell.App.Navigation;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.App;

public partial class MainWindow : Window
{
    // Segoe MDL2 Assets glyph code points (works on Win10 1809+ / Win11):
    //   0xE922 = ChromeMaximize
    //   0xE923 = ChromeRestore (shown while window is already maximized)
    private static readonly string GlyphMaximize = char.ConvertFromUtf32(0xE922);
    private static readonly string GlyphRestore  = char.ConvertFromUtf32(0xE923);

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnWindowStateChanged;
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
}

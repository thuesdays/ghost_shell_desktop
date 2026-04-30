// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

/// <summary>
/// Code-behind for the Logs page. The XAML is purely declarative;
/// the view layer adds two pieces of imperative behaviour that
/// don't fit cleanly into bindings:
///
///   • Smart auto-scroll — when AutoScroll is on, every newly-added
///     row triggers a ScrollIntoView on the last item. We hook the
///     ListBox's ItemsSource collection-changed event for that.
///     If the user manually scrolls up (away from the bottom) we
///     auto-disable AutoScroll so reading historical lines doesn't
///     fight the live tail.
///
///   • Keyboard shortcuts — Ctrl+L clear, Ctrl+P pause, Ctrl+F focus
///     the search box. Wired through routed-event handlers on the
///     UserControl.
/// </summary>
public partial class LogsView : UserControl
{
    private ScrollViewer? _scroll;
    private bool _userScrolledUp;

    /// <summary>
    /// Set true while we're about to programmatically move the
    /// scroll position (auto-snap to the latest entry, or layout
    /// shifts during Reproject). The next ScrollChanged event
    /// during this window is treated as our action, not user
    /// input, so we don't auto-disable AutoScroll.
    /// Cleared automatically on the dispatcher's next idle tick.
    /// </summary>
    private bool _suppressNextScrollAsUserInput;

    public LogsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LogList.ItemsSource is INotifyCollectionChanged ncc)
            ncc.CollectionChanged += OnItemsChanged;

        // Find the inner ScrollViewer once the template is applied.
        // ListBox lazily realises its template on first measure; by
        // Loaded that's already happened.
        _scroll = FindScrollViewer(LogList);
        if (_scroll is not null)
            _scroll.ScrollChanged += OnScrollChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (LogList.ItemsSource is INotifyCollectionChanged ncc)
            ncc.CollectionChanged -= OnItemsChanged;
        if (_scroll is not null)
            _scroll.ScrollChanged -= OnScrollChanged;
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not LogsViewModel vm) return;
        if (!vm.AutoScroll || _userScrolledUp) return;
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // ScrollIntoView mid-collection-change can throw if the
        // visual is being recycled. Defer to the dispatcher's
        // background priority so we run after layout settles.
        // Suppress the resulting ScrollChanged from being
        // interpreted as user-driven scroll (which would
        // auto-disable AutoScroll).
        _suppressNextScrollAsUserInput = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            try
            {
                if (LogList.Items.Count == 0) return;
                var last = LogList.Items[LogList.Items.Count - 1];
                LogList.ScrollIntoView(last);
            }
            catch { /* virtualization race — next add will catch up */ }
        });
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null) return;
        if (DataContext is not LogsViewModel vm) return;

        // Programmatic scroll (we just did a ScrollIntoView, or
        // layout shifted during a Reproject) — eat this event so
        // it doesn't get classified as "user wants to read history"
        // and turn off AutoScroll behind their back.
        if (_suppressNextScrollAsUserInput)
        {
            _suppressNextScrollAsUserInput = false;
            return;
        }

        // "At the bottom" with a 4-pixel tolerance — the ListBox
        // sometimes lands a fraction off when items resize during
        // layout.
        var atBottom = _scroll.VerticalOffset + _scroll.ViewportHeight
                       >= _scroll.ExtentHeight - 4;

        // If the user explicitly scrolled (their input changed
        // VerticalOffset), and they're now NOT at the bottom, treat
        // that as "I want to read history" and stop fighting them.
        if (e.VerticalChange != 0)
        {
            if (atBottom)  _userScrolledUp = false;
            else           _userScrolledUp = true;
        }

        // Reflect the latched flag back to the VM so the auto-scroll
        // checkbox visually toggles off, signalling to the user
        // why new lines aren't snapping to view anymore. We DO NOT
        // re-enable here — that's the user's call via the checkbox.
        if (_userScrolledUp && vm.AutoScroll) vm.AutoScroll = false;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    // Keyboard shortcuts — invoked from XAML's UserControl.InputBindings
    // is the standard pattern, but we go through PreviewKeyDown here so
    // they fire even when focus is inside the ListBox (which would
    // swallow KeyDown events for arrow / page navigation).
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (DataContext is not LogsViewModel vm) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        switch (e.Key)
        {
            case Key.L:
                vm.ClearCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.P:
                vm.TogglePauseCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}

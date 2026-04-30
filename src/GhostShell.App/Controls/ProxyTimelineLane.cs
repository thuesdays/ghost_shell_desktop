// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GhostShell.Core.Models;

namespace GhostShell.App.Controls;

/// <summary>
/// Single horizontal "lane" for one proxy on the timeline. Renders
/// each <see cref="ProxyHealthEvent"/> as a coloured dot whose X
/// position is its timestamp's fraction within
/// [<see cref="RangeStart"/> .. <see cref="RangeEnd"/>].
///
/// Implemented as a code-only Canvas-derived control so positions
/// recalculate cheaply on size changes — no Bindings/converters in
/// the hot path. Each colour matches the legend in the page header
/// (rotation = info blue, captcha = err red, burn = warn amber,
/// firstseen = ok green).
/// </summary>
public sealed class ProxyTimelineLane : Canvas
{
    public ProxyTimelineLane()
    {
        Background       = (Brush)Application.Current.Resources["BgDeep"];
        Height           = 28;
        ClipToBounds     = true;
        SnapsToDevicePixels = true;
        SizeChanged += (_, __) => Redraw();
    }

    // ─── Dependency properties ─────────────────────────────────────

    public static readonly DependencyProperty EventsProperty =
        DependencyProperty.Register(nameof(Events), typeof(IEnumerable),
            typeof(ProxyTimelineLane),
            new PropertyMetadata(null, OnEventsChanged));

    public IEnumerable? Events
    {
        get => (IEnumerable?)GetValue(EventsProperty);
        set => SetValue(EventsProperty, value);
    }

    public static readonly DependencyProperty RangeStartProperty =
        DependencyProperty.Register(nameof(RangeStart), typeof(DateTime),
            typeof(ProxyTimelineLane),
            new PropertyMetadata(DateTime.MinValue, (d, _) => ((ProxyTimelineLane)d).Redraw()));

    public DateTime RangeStart
    {
        get => (DateTime)GetValue(RangeStartProperty);
        set => SetValue(RangeStartProperty, value);
    }

    public static readonly DependencyProperty RangeEndProperty =
        DependencyProperty.Register(nameof(RangeEnd), typeof(DateTime),
            typeof(ProxyTimelineLane),
            new PropertyMetadata(DateTime.MinValue, (d, _) => ((ProxyTimelineLane)d).Redraw()));

    public DateTime RangeEnd
    {
        get => (DateTime)GetValue(RangeEndProperty);
        set => SetValue(RangeEndProperty, value);
    }

    private static void OnEventsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var lane = (ProxyTimelineLane)d;
        if (e.OldValue is INotifyCollectionChanged oldNotify)
            oldNotify.CollectionChanged -= lane.OnEventsCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged newNotify)
            newNotify.CollectionChanged += lane.OnEventsCollectionChanged;
        lane.Redraw();
    }

    private void OnEventsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => Dispatcher.BeginInvoke(Redraw);

    // ─── Rendering ─────────────────────────────────────────────────

    private void Redraw()
    {
        Children.Clear();
        if (Events is null || ActualWidth <= 0) return;
        if (RangeEnd <= RangeStart) return;

        var rangeTicks = (RangeEnd - RangeStart).Ticks;
        var width = ActualWidth;
        var midY  = ActualHeight / 2;

        // Faint "today" gridline at the right edge so users instantly
        // see where the timeline ends.
        var today = new Line
        {
            X1 = width - 0.5, X2 = width - 0.5,
            Y1 = 0, Y2 = ActualHeight,
            Stroke = (Brush)Application.Current.Resources["Border"],
            StrokeThickness = 1,
        };
        Children.Add(today);

        foreach (var obj in Events)
        {
            if (obj is not ProxyHealthEvent ev) continue;
            if (ev.At < RangeStart || ev.At > RangeEnd) continue;

            var fraction = (double)(ev.At - RangeStart).Ticks / rangeTicks;
            var x = Math.Clamp(fraction * width, 4, width - 4);

            var dot = new Ellipse
            {
                Width  = 8,
                Height = 8,
                Fill   = ColorForKind(ev.Kind),
                Stroke = Brushes.Transparent,
                ToolTip = $"{ev.Kind} · {ev.At.ToLocalTime():yyyy-MM-dd HH:mm}" +
                          (string.IsNullOrEmpty(ev.Detail) ? "" : $"\n{ev.Detail}"),
            };
            SetLeft(dot, x - 4);
            SetTop(dot, midY - 4);
            Children.Add(dot);
        }
    }

    private static Brush ColorForKind(ProxyHealthEventKind kind) => kind switch
    {
        ProxyHealthEventKind.Rotation  => (Brush)Application.Current.Resources["InfoBrush"],
        ProxyHealthEventKind.Captcha   => (Brush)Application.Current.Resources["ErrBrush"],
        ProxyHealthEventKind.Burn      => (Brush)Application.Current.Resources["WarnBrush"],
        ProxyHealthEventKind.FirstSeen => (Brush)Application.Current.Resources["OkBrush"],
        _                              => Brushes.Gray,
    };
}

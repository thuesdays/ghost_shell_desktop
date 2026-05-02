// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GhostShell.App.ViewModels;
using GhostShell.Core.Models;

namespace GhostShell.App.Views;

public partial class TrafficView : UserControl
{
    public TrafficView()
    {
        InitializeComponent();
        // Repaint the chart whenever the VM's series collection changes.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TrafficViewModel vm)
            {
                if (vm.Series is INotifyCollectionChanged ncc)
                    ncc.CollectionChanged += (_, _) => RedrawChart(vm);
                RedrawChart(vm);
            }
        };
        SizeChanged += (_, _) =>
        {
            if (DataContext is TrafficViewModel vm) RedrawChart(vm);
        };
    }

    /// <summary>Redraw the simple Polyline-based bytes / requests dual
    /// chart on the Canvas (named ChartCanvas in XAML). We intentionally
    /// don't depend on an external charting library — this 100-LOC
    /// implementation handles the dashboard's whole visual budget
    /// (a bytes line + a requests line). For richer charts later we
    /// can drop in OxyPlot / ScottPlot without disturbing the VM.</summary>
    private void RedrawChart(TrafficViewModel vm)
    {
        if (ChartCanvas is null) return;
        ChartCanvas.Children.Clear();

        var pts = vm.Series.ToList();
        if (pts.Count < 2)
        {
            // Single-point or empty — show a hint so the user knows
            // the chart is alive but underfed.
            var tb = new TextBlock
            {
                Text = pts.Count == 0
                    ? "No traffic recorded in this range yet."
                    : "Only one data point — chart needs at least two.",
                FontSize = 11,
                Foreground = (Brush)(TryFindResource("TextDim") ?? Brushes.Gray),
            };
            Canvas.SetLeft(tb, 12);
            Canvas.SetTop(tb, 12);
            ChartCanvas.Children.Add(tb);
            return;
        }

        var w = ChartCanvas.ActualWidth;
        var h = ChartCanvas.ActualHeight;
        if (w < 4 || h < 4) return;

        const double padL = 48;   // left axis labels
        const double padR = 48;   // right axis labels
        const double padT = 12;
        const double padB = 28;
        var plotW = Math.Max(1, w - padL - padR);
        var plotH = Math.Max(1, h - padT - padB);

        long maxBytes = 1, maxReqs = 1;
        foreach (var p in pts)
        {
            if (p.Bytes    > maxBytes) maxBytes = p.Bytes;
            if (p.Requests > maxReqs)  maxReqs  = p.Requests;
        }

        // Subtle gridlines (4 horizontal). Drawn first so the lines
        // sit on top.
        var gridBrush = (Brush)(TryFindResource("Border") ?? Brushes.DarkGray);
        for (int i = 0; i <= 4; i++)
        {
            var y = padT + plotH * i / 4.0;
            ChartCanvas.Children.Add(new Line
            {
                X1 = padL, Y1 = y, X2 = padL + plotW, Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 0.5,
                Opacity = 0.4,
            });
            // Left axis: bytes
            var bytesLabel = (long)(maxBytes * (4 - i) / 4.0);
            var lblL = new TextBlock
            {
                Text = TrafficViewModel.FormatBytes(bytesLabel),
                FontSize = 9,
                Foreground = (Brush)(TryFindResource("TextMuted") ?? Brushes.Gray),
            };
            Canvas.SetLeft(lblL, 0);
            Canvas.SetTop(lblL, y - 7);
            ChartCanvas.Children.Add(lblL);
            // Right axis: requests
            var reqLabel = (long)(maxReqs * (4 - i) / 4.0);
            var lblR = new TextBlock
            {
                Text = reqLabel.ToString("N0"),
                FontSize = 9,
                Foreground = (Brush)(TryFindResource("TextMuted") ?? Brushes.Gray),
            };
            Canvas.SetLeft(lblR, padL + plotW + 4);
            Canvas.SetTop(lblR, y - 7);
            ChartCanvas.Children.Add(lblR);
        }

        // Bytes line (filled area).
        var bytesLine = new Polyline
        {
            Stroke = (Brush)(TryFindResource("Accent") ?? Brushes.DodgerBlue),
            StrokeThickness = 2,
        };
        var bytesArea = new Polygon
        {
            Fill = new SolidColorBrush(((SolidColorBrush)bytesLine.Stroke).Color)
            {
                Opacity = 0.18,
            },
        };
        var areaPts = new PointCollection();
        areaPts.Add(new Point(padL, padT + plotH));
        for (int i = 0; i < pts.Count; i++)
        {
            var x = padL + plotW * i / (double)(pts.Count - 1);
            var y = padT + plotH - plotH * pts[i].Bytes / (double)maxBytes;
            bytesLine.Points.Add(new Point(x, y));
            areaPts.Add(new Point(x, y));
        }
        areaPts.Add(new Point(padL + plotW, padT + plotH));
        bytesArea.Points = areaPts;
        ChartCanvas.Children.Add(bytesArea);
        ChartCanvas.Children.Add(bytesLine);

        // Requests line (dashed, on top of bytes).
        var reqsLine = new Polyline
        {
            Stroke = (Brush)(TryFindResource("HueAmber") ?? Brushes.Orange),
            StrokeThickness = 1.6,
            StrokeDashArray = new DoubleCollection { 4, 3 },
        };
        for (int i = 0; i < pts.Count; i++)
        {
            var x = padL + plotW * i / (double)(pts.Count - 1);
            var y = padT + plotH - plotH * pts[i].Requests / (double)maxReqs;
            reqsLine.Points.Add(new Point(x, y));
        }
        ChartCanvas.Children.Add(reqsLine);

        // X-axis: first + middle + last labels (avoid overlap).
        var xMuted = (Brush)(TryFindResource("TextMuted") ?? Brushes.Gray);
        var xLabels = new[] { 0, pts.Count / 2, pts.Count - 1 }.Distinct().ToArray();
        foreach (var i in xLabels)
        {
            var x = padL + plotW * i / (double)(pts.Count - 1);
            var lbl = new TextBlock
            {
                Text = ShortenTime(pts[i].Time),
                FontSize = 9,
                Foreground = xMuted,
                FontFamily = (FontFamily)(TryFindResource("FontMono") ?? new FontFamily("Consolas")),
            };
            // Phase 28 audit fix — clamp on BOTH sides so the rightmost
            // label doesn't bleed past the canvas edge on narrow windows.
            var leftPx = Math.Max(0, Math.Min(w - 64, x - 30));
            Canvas.SetLeft(lbl, leftPx);
            Canvas.SetTop(lbl, padT + plotH + 4);
            ChartCanvas.Children.Add(lbl);
        }

        // Legend — top-left corner.
        var legendStack = new StackPanel { Orientation = Orientation.Horizontal };
        legendStack.Children.Add(MakeLegendDot((Brush)(TryFindResource("Accent") ?? Brushes.DodgerBlue), "bytes"));
        legendStack.Children.Add(new TextBlock { Text = "  ·  ", Foreground = xMuted, FontSize = 10 });
        legendStack.Children.Add(MakeLegendDot((Brush)(TryFindResource("HueAmber") ?? Brushes.Orange), "requests"));
        Canvas.SetLeft(legendStack, padL + 4);
        Canvas.SetTop(legendStack, padT - 2);
        ChartCanvas.Children.Add(legendStack);
    }

    private static FrameworkElement MakeLegendDot(Brush color, string label)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 10, Height = 3,
            Fill = color, Margin = new Thickness(0, 5, 4, 0),
        });
        sp.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 10,
            Foreground = color,
        });
        return sp;
    }

    private static string ShortenTime(string bucket)
    {
        // "2026-05-02 14" → "05-02 14h"
        // "2026-05-02"    → "05-02"
        if (bucket.Length >= 13)
            return bucket.Substring(5, 5) + " " + bucket.Substring(11, 2) + "h";
        if (bucket.Length >= 10)
            return bucket.Substring(5, 5);
        return bucket;
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public partial class CompetitorsView : UserControl
{
    public CompetitorsView()
    {
        InitializeComponent();
        // Repaint the chart whenever the VM's series collection changes.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is CompetitorsViewModel vm)
            {
                if (vm.ChartSeries is INotifyCollectionChanged ncc)
                    ncc.CollectionChanged += (_, _) => RedrawChart(vm);
                RedrawChart(vm);
            }
        };
        SizeChanged += (_, _) =>
        {
            if (DataContext is CompetitorsViewModel vm) RedrawChart(vm);
        };
    }

    private void TabLeaderboard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CompetitorsViewModel vm)
        {
            vm.LeaderboardSelected = true;
            vm.ByDomainSelected = false;
            vm.ByQuerySelected = false;
            vm.RecentAdsSelected = false;
        }
    }

    private void TabByDomain_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CompetitorsViewModel vm)
        {
            vm.LeaderboardSelected = false;
            vm.ByDomainSelected = true;
            vm.ByQuerySelected = false;
            vm.RecentAdsSelected = false;
        }
    }

    private void TabByQuery_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CompetitorsViewModel vm)
        {
            vm.LeaderboardSelected = false;
            vm.ByDomainSelected = false;
            vm.ByQuerySelected = true;
            vm.RecentAdsSelected = false;
        }
    }

    private void TabRecentAds_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is CompetitorsViewModel vm)
        {
            vm.LeaderboardSelected = false;
            vm.ByDomainSelected = false;
            vm.ByQuerySelected = false;
            vm.RecentAdsSelected = true;
        }
    }

    /// <summary>
    /// Redraw the volume trend chart on the Canvas. Simple Polyline-based
    /// implementation with one line per domain.
    /// </summary>
    private void RedrawChart(CompetitorsViewModel vm)
    {
        if (ChartCanvas is null) return;
        ChartCanvas.Children.Clear();

        var series = vm.ChartSeries.ToList();
        if (series.Count == 0)
        {
            var tb = new TextBlock
            {
                Text = "No trend data yet — run a script to populate.",
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

        const double padL = 48;
        const double padR = 48;
        const double padT = 12;
        const double padB = 28;
        var plotW = Math.Max(1, w - padL - padR);
        var plotH = Math.Max(1, h - padT - padB);

        // Find min/max date and max count across all series
        DateTime? minDate = null, maxDate = null;
        int maxCount = 1;
        foreach (var s in series)
        {
            foreach (var (date, count) in s.Points)
            {
                if (minDate is null || date < minDate) minDate = date;
                if (maxDate is null || date > maxDate) maxDate = date;
                if (count > maxCount) maxCount = count;
            }
        }

        if (minDate is null || maxDate is null || minDate == maxDate)
        {
            var tb = new TextBlock { Text = "Insufficient data points.", FontSize = 11 };
            Canvas.SetLeft(tb, 12);
            Canvas.SetTop(tb, 12);
            ChartCanvas.Children.Add(tb);
            return;
        }

        var dateRange = ((DateTime)maxDate - (DateTime)minDate).TotalMilliseconds;

        // Draw subtle gridlines (4 horizontal)
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
            // Y-axis labels (count)
            var label = (int)(maxCount * (4 - i) / 4.0);
            var lbl = new TextBlock
            {
                Text = label.ToString("N0"),
                FontSize = 9,
                Foreground = (Brush)(TryFindResource("TextMuted") ?? Brushes.Gray),
            };
            Canvas.SetLeft(lbl, 4);
            Canvas.SetTop(lbl, y - 7);
            ChartCanvas.Children.Add(lbl);
        }

        // Draw one line per series
        foreach (var s in series)
        {
            var points = s.Points.ToList();
            if (points.Count < 2) continue;

            var line = new Polyline
            {
                Stroke = s.LineBrush,
                StrokeThickness = 2,
            };

            foreach (var (date, count) in points)
            {
                var t = (date - (DateTime)minDate).TotalMilliseconds / dateRange;
                var x = padL + plotW * t;
                var y = padT + plotH - plotH * count / (double)maxCount;
                line.Points.Add(new Point(x, y));
            }

            ChartCanvas.Children.Add(line);
        }

        // X-axis labels (3-5 evenly spaced date ticks)
        var xMuted = (Brush)(TryFindResource("TextMuted") ?? Brushes.Gray);
        var tickCount = Math.Min(5, Math.Max(3, (int)(plotW / 120)));
        for (int i = 0; i < tickCount; i++)
        {
            var t = (double)i / (tickCount - 1);
            var date = (DateTime)minDate + TimeSpan.FromMilliseconds(dateRange * t);
            var x = padL + plotW * t;

            var lbl = new TextBlock
            {
                Text = date.ToString("MM-dd"),
                FontSize = 9,
                Foreground = xMuted,
            };
            var leftPx = Math.Max(0, Math.Min(w - 40, x - 25));
            Canvas.SetLeft(lbl, leftPx);
            Canvas.SetTop(lbl, padT + plotH + 4);
            ChartCanvas.Children.Add(lbl);
        }

        // Legend (horizontal, color square + domain name)
        var legendY = padT + 2;
        var legendX = padL + 4;
        foreach (var s in series)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new Rectangle
            {
                Width = 10, Height = 3,
                Fill = s.LineBrush,
                Margin = new Thickness(0, 5, 6, 0),
            });
            sp.Children.Add(new TextBlock
            {
                Text = s.Domain,
                FontSize = 9,
                Foreground = s.LineBrush,
            });

            Canvas.SetLeft(sp, legendX);
            Canvas.SetTop(sp, legendY);
            ChartCanvas.Children.Add(sp);

            legendX += (sp.DesiredSize.Width > 0 ? sp.DesiredSize.Width : 60) + 12;
        }
    }
}

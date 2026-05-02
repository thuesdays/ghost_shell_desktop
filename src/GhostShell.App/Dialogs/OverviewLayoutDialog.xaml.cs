// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using GhostShell.Core.Models;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

public partial class OverviewLayoutDialog : Window
{
    private readonly IOverviewLayoutService _layoutService;
    private List<OverviewWidgetState> _currentLayout = new();

    public OverviewLayoutDialog(IOverviewLayoutService layoutService)
    {
        _layoutService = layoutService;
        InitializeComponent();
        _ = LoadLayoutAsync();
    }

    private async Task LoadLayoutAsync()
    {
        try
        {
            var widgets = await _layoutService.ListAsync();
            _currentLayout = widgets.ToList();
            BuildWidgetList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to load layout: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            DialogResult = false;
            Close();
        }
    }

    private void BuildWidgetList()
    {
        WidgetList.Children.Clear();

        foreach (var state in _currentLayout.OrderBy(s => s.Position))
        {
            var def = OverviewWidgetCatalog.Find(state.WidgetId);
            if (def == null) continue;

            var row = new Border
            {
                BorderBrush = (System.Windows.Media.Brush)FindResource("Border"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                // Thickness has only 1-arg (uniform) and 4-arg ctors —
                // no 2-arg (h, v) form like XAML's "0,8" shorthand.
                Padding = new Thickness(0, 8, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Icon + name + description
            var iconText = new TextBlock
            {
                Text = def.Icon,
                FontSize = 16,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(iconText, 0);
            grid.Children.Add(iconText);

            var textPanel = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var nameText = new TextBlock
            {
                Text = def.DisplayName,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("Text")
            };
            var descText = new TextBlock
            {
                Text = def.Description,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            };
            textPanel.Children.Add(nameText);
            textPanel.Children.Add(descText);
            Grid.SetColumn(textPanel, 1);
            grid.Children.Add(textPanel);

            // Checkbox
            var checkbox = new CheckBox
            {
                IsChecked = state.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            checkbox.Checked += (_, _) => UpdateWidgetEnabled(state.WidgetId, true);
            checkbox.Unchecked += (_, _) => UpdateWidgetEnabled(state.WidgetId, false);
            Grid.SetColumn(checkbox, 2);
            grid.Children.Add(checkbox);

            // Up/Down buttons
            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var btnUp = new Button
            {
                Content = "↑",
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 4, 0)
            };
            btnUp.Click += (_, _) => MoveWidget(state.WidgetId, -1);
            btnPanel.Children.Add(btnUp);

            var btnDown = new Button
            {
                Content = "↓",
                Width = 28,
                Height = 28,
                Padding = new Thickness(0)
            };
            btnDown.Click += (_, _) => MoveWidget(state.WidgetId, 1);
            btnPanel.Children.Add(btnDown);

            Grid.SetColumn(btnPanel, 3);
            grid.Children.Add(btnPanel);

            row.Child = grid;
            WidgetList.Children.Add(row);
        }
    }

    private void UpdateWidgetEnabled(string widgetId, bool enabled)
    {
        var idx = _currentLayout.FindIndex(s => s.WidgetId == widgetId);
        if (idx >= 0)
        {
            _currentLayout[idx] = _currentLayout[idx] with { Enabled = enabled };
        }
    }

    private void MoveWidget(string widgetId, int delta)
    {
        var idx = _currentLayout.FindIndex(s => s.WidgetId == widgetId);
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= _currentLayout.Count) return;

        var temp = _currentLayout[idx];
        _currentLayout[idx] = _currentLayout[newIdx];
        _currentLayout[newIdx] = temp;

        // Update positions
        for (int i = 0; i < _currentLayout.Count; i++)
        {
            _currentLayout[i] = _currentLayout[i] with { Position = i };
        }

        BuildWidgetList();
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _layoutService.SaveAsync(_currentLayout);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save layout: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void OnResetClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Reset to default layout?", "Confirm",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try
            {
                await _layoutService.ResetAsync();
                await LoadLayoutAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to reset layout: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

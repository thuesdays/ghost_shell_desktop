// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Lightweight "pick an action" picker used by the Phase 18 nested-
/// step add flow. Users tap "+ add inside" on a foreach/if/while
/// container and this dialog pops up listing every action type from
/// the parent's palette catalogue, with a filter at the top.
///
/// Implemented programmatically (no .xaml) to avoid registering yet
/// another XAML pair in the project file. Visual style is intentionally
/// plain — the parent's theme provides colours via dynamic resource
/// lookups.
/// </summary>
public sealed class ScriptActionPickerDialog : Window
{
    public string? SelectedType { get; private set; }

    private readonly ListBox _list;
    private readonly TextBox _filter;
    private readonly List<ActionItem> _all;

    public ScriptActionPickerDialog(
        IEnumerable<(string Type, string Icon, string Label, string Description, string Group)> items)
    {
        Title = "Add nested action";
        Width = 460;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        _all = items
            .Select(i => new ActionItem(i.Type, i.Icon, i.Label, i.Description, i.Group))
            .ToList();

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Filter
        _filter = new TextBox
        {
            FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 8),
        };
        _filter.SetResourceReference(BackgroundProperty, "BgRaised");
        _filter.SetResourceReference(ForegroundProperty, "Text");
        _filter.SetResourceReference(BorderBrushProperty, "Border");
        _filter.TextChanged += (_, _) => Refresh();
        _filter.KeyDown += OnFilterKey;
        Grid.SetRow(_filter, 0);
        root.Children.Add(_filter);

        // List
        _list = new ListBox
        {
            BorderThickness = new Thickness(1),
        };
        _list.SetResourceReference(BackgroundProperty, "BgRaised");
        _list.SetResourceReference(BorderBrushProperty, "Border");
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        };
        _list.ItemTemplate = BuildItemTemplate();
        Grid.SetRow(_list, 1);
        root.Children.Add(_list);

        // Buttons
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 84,
            Margin = new Thickness(0, 0, 8, 0),
            // IsCancel=true wires Esc to close-without-result automatically.
            IsCancel = true,
        };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new Button
        {
            Content = "Add",
            MinWidth = 100,
            // IsDefault=true wires Enter to fire Click when no other
            // control consumes the key (filter / list both already have
            // explicit handlers, so this only matters for stray focus).
            IsDefault = true,
        };
        ok.SetResourceReference(StyleProperty, "ButtonPrimary");
        ok.Click += (_, _) => Accept();
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;

        Refresh();
        Loaded += (_, _) => _filter.Focus();
    }

    private void OnFilterKey(object sender, KeyEventArgs e)
    {
        // Down-arrow from the filter jumps focus into the list so the
        // keyboard flow is filter → arrow-keys to pick → Enter to
        // confirm without ever touching the mouse.
        if (e.Key == Key.Down)
        {
            if (_list.Items.Count > 0)
            {
                _list.SelectedIndex = 0;
                _list.Focus();
                if (_list.ItemContainerGenerator.ContainerFromIndex(0)
                    is ListBoxItem lbi) lbi.Focus();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void Accept()
    {
        if (_list.SelectedItem is ActionItem it)
        {
            SelectedType = it.Type;
            DialogResult = true;
            Close();
        }
    }

    private void Refresh()
    {
        var needle = (_filter.Text ?? "").Trim();
        var src = string.IsNullOrEmpty(needle)
            ? _all
            : _all.Where(a =>
                a.Type.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || a.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || a.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        _list.ItemsSource = src;
        if (src.Count > 0 && _list.SelectedItem is null)
            _list.SelectedIndex = 0;
    }

    private static DataTemplate BuildItemTemplate()
    {
        // Two-column row: icon docked Left, label+subline stacked
        // vertically in the remaining space. DockPanel avoids the
        // ColumnDefinitions-via-FrameworkElementFactory limitation
        // (FEF doesn't expose collection setters for Grid.ColumnDefinitions).
        var dock = new FrameworkElementFactory(typeof(DockPanel));
        dock.SetValue(DockPanel.LastChildFillProperty, true);
        dock.SetValue(MarginProperty, new Thickness(6, 4, 6, 4));

        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Icon"));
        icon.SetValue(TextBlock.FontSizeProperty, 16.0);
        icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
        icon.SetValue(DockPanel.DockProperty, Dock.Left);

        var stack = new FrameworkElementFactory(typeof(StackPanel));

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
        label.SetValue(TextBlock.FontSizeProperty, 13.0);
        label.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        label.SetResourceReference(TextBlock.ForegroundProperty, "Text");

        var sub = new FrameworkElementFactory(typeof(TextBlock));
        sub.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("SubLine"));
        sub.SetValue(TextBlock.FontSizeProperty, 10.5);
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        stack.AppendChild(label);
        stack.AppendChild(sub);
        dock.AppendChild(icon);
        dock.AppendChild(stack);

        return new DataTemplate { VisualTree = dock };
    }

    private sealed record ActionItem(string Type, string Icon, string Label, string Description, string Group)
    {
        /// <summary>"GROUP · description" — sub-line under the bold label.</summary>
        public string SubLine => string.IsNullOrEmpty(Description)
            ? Group
            : $"{Group}  ·  {Description}";
    }
}

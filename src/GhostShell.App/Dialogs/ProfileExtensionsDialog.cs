// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GhostShell.Core.Models;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 27 — per-profile extension picker. Opens for one profile and
/// shows every installed extension with three states:
///   • inherit (use the global default for new profiles)
///   • on  (force enabled regardless of global)
///   • off (force disabled regardless of global)
///
/// Saving writes a row in <c>profile_extensions</c> for explicit
/// on/off; "inherit" deletes the row so the profile follows the
/// global default.
/// </summary>
public sealed class ProfileExtensionsDialog : Window
{
    private readonly IExtensionService _service;
    private readonly string _profileName;
    private readonly List<RowVm> _rows = new();
    public bool Saved { get; private set; }

    public ProfileExtensionsDialog(IExtensionService service, string profileName)
    {
        _service = service;
        _profileName = profileName;
        Title = $"Extensions for '{profileName}'";
        Width = 600; Height = 600;
        // Phase 27 audit fix — anchor to MainWindow so the modal
        // doesn't fall behind the app on Windows. CenterOwner needs
        // the Owner to actually be set.
        Owner = System.Windows.Application.Current?.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BgDeep");

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var title = new TextBlock { Text = "🧩  Per-profile extensions", FontSize = 16, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        head.Children.Add(title);
        var sub = new TextBlock
        {
            Text = $"Override which extensions load when '{profileName}' starts. 'Inherit' falls back to the global default set on the Extensions page.",
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "WarnBrush");
        head.Children.Add(sub);
        Grid.SetRow(head, 0);
        root.Children.Add(head);

        // List
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var list = new ItemsControl();
        scroll.Content = list;
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        // Footer
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var cancel = new Button { Content = "Cancel", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var save = new Button { Content = "Save", MinWidth = 100, IsDefault = true };
        save.SetResourceReference(StyleProperty, "ButtonPrimary");
        save.Click += async (_, _) => await SaveAsync();
        btns.Children.Add(cancel);
        btns.Children.Add(save);
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        Content = root;

        // Populate after the window is wired up so we can dispatch on
        // the UI thread directly.
        Loaded += async (_, _) => await LoadAsync(list);
    }

    private async Task LoadAsync(ItemsControl list)
    {
        var all = await _service.ListAsync();
        var overrides = await _service.GetProfileOverridesAsync(_profileName);
        list.Items.Clear();
        _rows.Clear();
        foreach (var ext in all)
        {
            var initial = overrides.TryGetValue(ext.Id, out var b)
                ? (b is null ? ProfileExtState.Inherit : (b.Value ? ProfileExtState.On : ProfileExtState.Off))
                : ProfileExtState.Inherit;
            var row = new RowVm(ext, initial);
            _rows.Add(row);
            list.Items.Add(BuildRowView(row));
        }
    }

    private FrameworkElement BuildRowView(RowVm row)
    {
        var border = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(4),
        };
        border.SetResourceReference(Border.BackgroundProperty, "BgDeep");
        border.SetResourceReference(Border.BorderBrushProperty, "Border");
        border.BorderThickness = new Thickness(1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var info = new StackPanel();
        var name = new TextBlock { Text = row.Item.Name, FontSize = 13, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        info.Children.Add(name);
        var meta = new TextBlock
        {
            Text = "v" + row.Item.Version + "  ·  global: " + (row.Item.Enabled ? "on" : "off"),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
        };
        meta.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        info.Children.Add(meta);
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var picker = new ComboBox
        {
            MinWidth = 110,
            VerticalAlignment = VerticalAlignment.Center,
        };
        picker.Items.Add("Inherit");
        picker.Items.Add("Force on");
        picker.Items.Add("Force off");
        picker.SelectedIndex = (int)row.State;
        picker.SelectionChanged += (_, _) =>
        {
            row.State = (ProfileExtState)picker.SelectedIndex;
        };
        Grid.SetColumn(picker, 1);
        grid.Children.Add(picker);

        border.Child = grid;
        return border;
    }

    private async Task SaveAsync()
    {
        try
        {
            foreach (var row in _rows)
            {
                switch (row.State)
                {
                    case ProfileExtState.Inherit:
                        await _service.ClearProfileOverrideAsync(_profileName, row.Item.Id);
                        break;
                    case ProfileExtState.On:
                        await _service.SetEnabledForProfileAsync(_profileName, row.Item.Id, true);
                        break;
                    case ProfileExtState.Off:
                        await _service.SetEnabledForProfileAsync(_profileName, row.Item.Id, false);
                        break;
                }
            }
            Saved = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private enum ProfileExtState { Inherit = 0, On = 1, Off = 2 }

    private sealed class RowVm
    {
        public ExtensionItem Item { get; }
        public ProfileExtState State { get; set; }
        public RowVm(ExtensionItem item, ProfileExtState s) { Item = item; State = s; }
    }
}

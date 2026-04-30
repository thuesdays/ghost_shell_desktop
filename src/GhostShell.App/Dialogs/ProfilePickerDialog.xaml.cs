// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Multi-select profile picker. Filterable list with per-row
/// checkboxes + Select-all / None links. Returns the selected
/// names via <see cref="SelectedNames"/>.
/// </summary>
public partial class ProfilePickerDialog : Window
{
    private readonly ObservableCollection<Row> _rows = new();
    private ICollectionView _view;

    public IReadOnlyList<string> SelectedNames { get; private set; } = Array.Empty<string>();

    public ProfilePickerDialog(string title, string subtitle,
        IEnumerable<string> profileNames, IEnumerable<string>? preChecked = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        SubText.Text   = subtitle;
        var preset = preChecked is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(preChecked, StringComparer.OrdinalIgnoreCase);
        foreach (var n in profileNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var row = new Row { Name = n, IsChecked = preset.Contains(n) };
            row.PropertyChanged += (_, _) => UpdateCount();
            _rows.Add(row);
        }
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = FilterRow;
        ProfileList.ItemsSource = _view;
        UpdateCount();
    }

    private bool FilterRow(object o)
    {
        if (o is not Row r) return false;
        var filter = FilterField?.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(filter)) return true;
        return r.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => _view.Refresh();

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        // Tick all rows currently visible — respects the filter.
        foreach (var r in _view.Cast<Row>()) r.IsChecked = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var r in _view.Cast<Row>()) r.IsChecked = false;
    }

    private void UpdateCount()
    {
        // Phase 14 audit fix: report visible-count when the user has
        // a filter that hides every checked row, so they understand
        // why the Apply button stayed enabled. We still gate Apply
        // on the underlying count of CHECKED rows (filter-independent),
        // because the user's intent is "apply to my checked set",
        // even if they can't see them right now.
        var totalChecked   = _rows.Count(r => r.IsChecked);
        var visibleChecked = _view.Cast<Row>().Count(r => r.IsChecked);
        if (totalChecked == 0)
            CountText.Text = "No profiles selected — Apply disabled";
        else if (visibleChecked < totalChecked)
            CountText.Text = $"{totalChecked} selected ({visibleChecked} visible under filter)";
        else
            CountText.Text = $"{totalChecked} selected";
        OkBtn.IsEnabled = totalChecked > 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        SelectedNames = _rows.Where(r => r.IsChecked).Select(r => r.Name).ToList();
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private sealed class Row : INotifyPropertyChanged
    {
        public required string Name { get; init; }
        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (_isChecked != value) { _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked))); } }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}

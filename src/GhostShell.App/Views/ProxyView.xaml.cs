// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;

namespace GhostShell.App.Views;

public partial class ProxyView : UserControl
{
    public ProxyView() => InitializeComponent();

    /// <summary>
    /// Phase 70 — header-checkbox "select all / clear all" handler. Reads
    /// the click target's IsChecked, then drives DataGrid.SelectAll() or
    /// UnselectAll() to toggle every row's IsSelected. The per-row checkboxes
    /// in each cell already bind two-way to DataGridRow.IsSelected, so they
    /// re-render automatically as the rows tick on/off.
    /// </summary>
    private void OnProxySelectAllClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var grid = ProxyGrid;
        if (grid is null) return;
        if (cb.IsChecked == true) grid.SelectAll();
        else                       grid.UnselectAll();
    }
}

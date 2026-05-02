// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using GhostShell.Core.Models;

namespace GhostShell.App.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    private void OnAdDensityLoaded(object sender, RoutedEventArgs e)
    {
        // The ad density widget has been loaded. We don't need to do
        // anything special here for now — the sparkline can be added
        // later if needed. For now, just handle empty IP state in the UI.
    }
}

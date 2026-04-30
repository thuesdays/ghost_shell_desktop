// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Views;

public partial class ScriptsView : UserControl
{
    public ScriptsView() { InitializeComponent(); }

    /// <summary>
    /// Click-anywhere-on-card → Edit. Distinguishes from the ⋯
    /// button (which has its own handler) by checking that the
    /// originalSource isn't inside the button's visual tree —
    /// otherwise clicking ⋯ would both open the menu AND open
    /// the editor.
    /// </summary>
    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not ScriptCardVm card) return;
        // Bail if the click landed on the ⋯ button (it has its own handler).
        if (e.OriginalSource is DependencyObject src && IsInsideMenuButton(src)) return;
        if (DataContext is ScriptsViewModel vm)
        {
            if (vm.EditCommand.CanExecute(card))
                vm.EditCommand.Execute(card);
        }
    }

    private static bool IsInsideMenuButton(DependencyObject o)
    {
        // Walk up to find a Button whose content is the ellipsis glyph.
        // We can't tag it cleanly because the bound CommandParameter
        // is the card, so a content-string check is the cheapest gate.
        DependencyObject? cur = o;
        while (cur is not null)
        {
            if (cur is Button b && b.Content is "⋯") return true;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return false;
    }

    /// <summary>
    /// ⋯ button → open the card's context menu programmatically.
    /// </summary>
    private void OnMenuButton(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        // The Border that owns the menu is the visual ancestor of
        // the button. Walk up until we find a FrameworkElement that
        // has ContextMenu set.
        DependencyObject? cur = b;
        while (cur is not null)
        {
            if (cur is FrameworkElement fe && fe.ContextMenu is not null)
            {
                fe.ContextMenu.PlacementTarget = fe;
                fe.ContextMenu.IsOpen = true;
                return;
            }
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
    }
}

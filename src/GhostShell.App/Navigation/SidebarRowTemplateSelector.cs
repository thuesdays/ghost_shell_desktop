// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using GhostShell.App.ViewModels;

namespace GhostShell.App.Navigation;

/// <summary>
/// Picks one of two DataTemplates for sidebar rows: a slim section
/// label (no interactivity) vs a clickable nav item. Avoids stuffing
/// triggers / DataTemplate-in-Style nesting into XAML which the
/// compiler refuses to validate cleanly.
/// </summary>
public sealed class SidebarRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SectionTemplate { get; set; }
    public DataTemplate? ItemTemplate    { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is SidebarRow row)
            return row.IsSection ? SectionTemplate : ItemTemplate;
        return base.SelectTemplate(item, container);
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 23 — script templates gallery. The user picks a template,
/// the calling ViewModel turns it into a fresh <see cref="Script"/>
/// and opens the editor for further customisation.
///
/// Selection state is one of (filter, category, picked-template). The
/// dialog filters the master catalog locally — no DB round-trip.
/// </summary>
public partial class ScriptTemplatesDialog : Window
{
    /// <summary>Set when the user clicks "Use template". Caller reads
    /// this and seeds a new Script from it.</summary>
    public ScriptTemplate? Selected { get; private set; }

    private readonly ObservableCollection<ScriptTemplate> _visible = new();
    private string _activeCategory = "All";

    public ScriptTemplatesDialog()
    {
        InitializeComponent();
        // Categories are pulled from the catalog plus a synthetic
        // "All" first entry that clears the filter.
        var cats = new List<string> { "All" };
        cats.AddRange(ScriptTemplateCatalog.Categories);
        CategoryList.ItemsSource = cats;
        TemplateList.ItemsSource = _visible;
        ApplyFilter();
        StatusText.Text = $"{_visible.Count} templates · pick one and click 'Use template'";
    }

    // ─── Filtering ────────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter();

    private void OnCategoryClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string cat)
        {
            _activeCategory = cat;
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var needle = (SearchField?.Text ?? "").Trim();
        _visible.Clear();
        foreach (var t in ScriptTemplateCatalog.All)
        {
            if (_activeCategory != "All"
                && !string.Equals(t.Category, _activeCategory, StringComparison.OrdinalIgnoreCase))
                continue;
            if (needle.Length > 0
                && !t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !t.Description.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && !t.Category.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;
            _visible.Add(t);
        }
        StatusText.Text = $"{_visible.Count} templates match";
    }

    // ─── Selection / preview ──────────────────────────────────────

    private void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border b || b.Tag is not ScriptTemplate t) return;
        Selected = t;
        UseBtn.IsEnabled = true;
        PreviewName.Text = t.Name;
        PreviewDesc.Text = t.Description;
        PreviewSteps.Text = $"{CountSteps(t.StepsJson)} steps · category: {t.Category}";
        PreviewJson.Text = PrettyJson(t.StepsJson);
    }

    private static int CountSteps(string stepsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(stepsJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }

    private static string PrettyJson(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch { return raw; }
    }

    // ─── Footer ──────────────────────────────────────────────────

    private void OnUse(object sender, RoutedEventArgs e)
    {
        if (Selected is null) return;
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

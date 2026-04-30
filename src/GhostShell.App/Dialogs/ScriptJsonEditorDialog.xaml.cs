// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// JSON-based script editor (Phase 12 iter 3 minimum-viable).
/// Drag-drop graph editor with palette/canvas/inspector lands in
/// Phase 13 — for now power users edit the JSON directly with live
/// validation feedback in the title bar.
/// </summary>
public partial class ScriptJsonEditorDialog : Window
{
    public Script? Result { get; private set; }
    public string? ResultExpectedEtag { get; private set; }

    private readonly Script? _existing;

    public ScriptJsonEditorDialog(Script? existing)
    {
        InitializeComponent();
        _existing = existing;
        if (existing is not null)
        {
            TitleText.Text          = $"Edit script #{existing.Id}";
            NameField.Text          = existing.Name;
            DescriptionField.Text   = existing.Description ?? "";
            JsonField.Text          = PrettyPrint(existing.StepsJson);
            EnabledCheck.IsChecked  = existing.Enabled;
            DefaultCheck.IsChecked  = existing.IsDefault;
        }
        else
        {
            JsonField.Text = """
                [
                  {
                    "type": "navigate",
                    "params": { "url": "https://example.com/" }
                  },
                  {
                    "type": "dwell",
                    "params": { "min_ms": 2000, "max_ms": 5000 }
                  }
                ]
                """;
        }
    }

    private static string PrettyPrint(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
        }
        catch
        {
            return json;
        }
    }

    private void OnJsonChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonField.Text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("top-level value must be an array");
            ValidationStatus.Text = "✓ valid";
            ValidationStatus.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "OkBrush");
            SaveBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            ValidationStatus.Text = "✗ " + ex.Message;
            ValidationStatus.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "ErrBrush");
            SaveBtn.IsEnabled = false;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = (NameField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "Name is required.", "Save",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Re-validate JSON to be sure (the live validator drops Save
        // when invalid, but defence-in-depth).
        string compact;
        try
        {
            using var doc = JsonDocument.Parse(JsonField.Text);
            compact = JsonSerializer.Serialize(doc.RootElement);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "JSON invalid: " + ex.Message, "Save",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ResultExpectedEtag = _existing?.ETag;
        Result = new Script
        {
            Id          = _existing?.Id ?? 0,
            Name        = name,
            Description = (DescriptionField.Text ?? "").Trim(),
            StepsJson   = compact,
            Enabled     = EnabledCheck.IsChecked == true,
            IsDefault   = DefaultCheck.IsChecked == true,
            ETag        = _existing?.ETag ?? "",
            CreatedAt   = _existing?.CreatedAt ?? default,
            UpdatedAt   = DateTime.UtcNow,
        };
        DialogResult = true;
        Close();
    }
}

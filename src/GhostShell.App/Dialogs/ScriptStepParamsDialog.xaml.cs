// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace GhostShell.App.Dialogs;

public partial class ScriptStepParamsDialog : Window
{
    public string? Result { get; private set; }

    public ScriptStepParamsDialog(string actionType, string currentJson)
    {
        InitializeComponent();
        TitleText.Text = $"Edit '{actionType}' params";
        // Pretty-print on open. Falls through if invalid; OnJsonChanged
        // will surface the error.
        try
        {
            using var doc = JsonDocument.Parse(currentJson);
            JsonField.Text = JsonSerializer.Serialize(doc.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            JsonField.Text = currentJson;
        }
    }

    private void OnJsonChanged(object sender, TextChangedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonField.Text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("must be a JSON object");
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

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonField.Text);
            Result = JsonSerializer.Serialize(doc.RootElement); // compact
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "JSON invalid: " + ex.Message,
                "Save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

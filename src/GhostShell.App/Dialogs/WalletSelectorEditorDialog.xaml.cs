// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.Json;
using System.Windows;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 57 — editor for the <c>wallet_selector_overrides</c> settings blob.
/// Validates the JSON before persisting so a typo can't silently disable all
/// wallet automation.
/// </summary>
public partial class WalletSelectorEditorDialog : Window
{
    private const string SettingsKey = "wallet_selector_overrides";
    private readonly ISettingsService _settings;

    private const string Example =
        "{\n" +
        "  \"metamask\": {\n" +
        "    \"extraSelectors\": {\n" +
        "      \"confirm\": [\"[data-testid='confirm-footer-button']\", \"button.mm-button-primary\"]\n" +
        "    }\n" +
        "  }\n" +
        "}";

    public WalletSelectorEditorDialog(ISettingsService settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            try { JsonBox.Text = await _settings.GetStringAsync(SettingsKey) ?? ""; }
            catch { JsonBox.Text = ""; }
        };
    }

    private void InsertExample_Click(object sender, RoutedEventArgs e)
        => JsonBox.Text = Example;

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        JsonBox.Text = "";
        try { await _settings.SetStringAsync(SettingsKey, null); StatusText.Text = "Reset — shipped defaults are in use."; }
        catch (System.Exception ex) { StatusText.Text = "Reset failed: " + ex.Message; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var txt = JsonBox.Text?.Trim() ?? "";
        if (txt.Length == 0)
        {
            try { await _settings.SetStringAsync(SettingsKey, null); } catch { /* best effort */ }
            DialogResult = true;
            return;
        }
        // Validate loudly — ParseOverrides is tolerant, so check the raw JSON here.
        try { using var _ = JsonDocument.Parse(txt); }
        catch (JsonException ex) { StatusText.Text = "Invalid JSON: " + ex.Message; return; }

        try
        {
            await _settings.SetStringAsync(SettingsKey, txt);
            DialogResult = true;
        }
        catch (System.Exception ex) { StatusText.Text = "Save failed: " + ex.Message; }
    }
}

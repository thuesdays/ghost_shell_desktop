// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Proxy create/edit modal — visual + structural match of the
/// legacy web project's "Add proxy" dialog: single URL field with
/// hint, optional rotation block, default-flag checkbox, notes,
/// auto-test toggle.
///
/// On Save: parse URL via <see cref="ProxyUrl.TryParse"/>, generate
/// a stable slug from the (optional) name when creating, hand the
/// resulting <see cref="Proxy"/> back via <see cref="Result"/>.
/// </summary>
public partial class ProxyEditorDialog : Window
{
    private readonly Proxy? _existing;

    public Proxy? Result { get; private set; }

    /// <summary>True when the user wants the runtime to test the proxy
    /// after Save. Read by the caller; not part of the Proxy model.</summary>
    public bool AutoTestAfterSave { get; private set; }

    /// <summary>
    /// Called when the user clicks "Rotate IP now". Receives the
    /// (already-saved) proxy slug + the rotation URL currently typed
    /// in the form, so the host VM can decide how to fire the rotation
    /// API and report back. Set by the caller before <c>ShowDialog</c>.
    /// </summary>
    public Func<string /* slug */, string /* rotateUrl */, Task<string?>>?
        OnRotateRequested { get; set; }

    public ProxyEditorDialog(Proxy? existing)
    {
        InitializeComponent();

        _existing = existing;

        if (existing is not null)
        {
            // ─── Edit mode ───
            TitleText.Text          = $"Edit proxy · {existing.Name ?? existing.Slug}";
            NameField.Text          = existing.Name ?? "";
            UrlField.Text           = existing.Url;
            IsRotatingField.IsChecked = existing.IsRotating;
            RotationUrlField.Text   = existing.RotationApiUrl ?? "";
            RotationKeyField.Text   = existing.RotationApiKey ?? "";
            IsDefaultField.IsChecked  = existing.IsDefault;
            NotesField.Text         = existing.Notes ?? "";
            AutoTestField.IsChecked = false; // edits don't auto-test by default
            SelectProvider(existing.RotationProvider ?? "none");
            RotationFields.Visibility = existing.IsRotating ? Visibility.Visible : Visibility.Collapsed;
            UpdateRotateNowVisibility();
        }
        else
        {
            // ─── Create mode ───
            TitleText.Text = "Add proxy";
            SelectProvider("none");
            UrlField.Focus();
        }
    }

    private void OnRotationUrlChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => UpdateRotateNowVisibility();

    private void OnRotationToggled(object sender, RoutedEventArgs e)
    {
        // Show/hide rotation block in lock-step with the checkbox.
        RotationFields.Visibility = IsRotatingField.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateRotateNowVisibility();
    }

    /// <summary>
    /// "Rotate IP now" appears only when ALL of these hold:
    ///   • we're editing an existing proxy (need a slug to call the API),
    ///   • rotation is enabled,
    ///   • a rotation URL is filled.
    /// Anything else and the button is hidden — clicking it would
    /// either fail validation or not have anything to call.
    /// </summary>
    private void UpdateRotateNowVisibility()
    {
        var canRotate =
            _existing is not null
            && IsRotatingField.IsChecked == true
            && !string.IsNullOrWhiteSpace(RotationUrlField.Text);

        RotateNowPanel.Visibility = canRotate ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnRotateNow(object sender, RoutedEventArgs e)
    {
        if (_existing is null || OnRotateRequested is null) return;
        var url = RotationUrlField.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url)) return;

        var btn = sender as Button;
        if (btn is not null) btn.IsEnabled = false;
        var origHint = RotateHint.Text;
        RotateHint.Text = "Rotating…";
        try
        {
            var msg = await OnRotateRequested(_existing.Slug, url);
            RotateHint.Text = msg ?? "Rotation triggered.";
        }
        catch (Exception ex)
        {
            RotateHint.Text = $"Rotation failed: {ex.Message}";
        }
        finally
        {
            if (btn is not null) btn.IsEnabled = true;
        }
    }

    private void SelectProvider(string tagValue)
    {
        foreach (ComboBoxItem item in ProviderCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), tagValue,
                              StringComparison.OrdinalIgnoreCase))
            {
                ProviderCombo.SelectedItem = item;
                return;
            }
        }
        ProviderCombo.SelectedIndex = 0;
    }

    private string SelectedProviderTag()
        => (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameField.Text?.Trim();
        var url  = UrlField.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(url))
        {
            ShowError("Proxy URL is required.");
            UrlField.Focus();
            return;
        }
        if (!ProxyUrl.TryParse(url, out _))
        {
            ShowError("Couldn't parse the URL. Expected host:port, user:pass@host:port, or scheme://… form.");
            UrlField.Focus();
            return;
        }

        var isRotating = IsRotatingField.IsChecked == true;
        if (isRotating && string.IsNullOrWhiteSpace(RotationUrlField.Text))
        {
            ShowError("Rotation API URL is required when rotation is enabled.");
            RotationUrlField.Focus();
            return;
        }

        // Slug logic: keep existing on edit, derive from name on create
        // (or fall back to a parsed-host slug, then to a timestamp).
        var slug = _existing?.Slug
                   ?? Slugify(name)
                   ?? Slugify(url)
                   ?? $"proxy-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        Result = new Proxy
        {
            Slug             = slug,
            Name             = NullIfBlank(name),
            Url              = url,
            IsRotating       = isRotating,
            RotationApiUrl   = isRotating ? NullIfBlank(RotationUrlField.Text) : null,
            RotationProvider = isRotating ? SelectedProviderTag() : null,
            RotationApiKey   = isRotating ? NullIfBlank(RotationKeyField.Text) : null,
            IsDefault        = IsDefaultField.IsChecked == true,
            Notes            = NullIfBlank(NotesField.Text),
            // Diagnostics state passes through unchanged on edit, blank on create
            LastIp           = _existing?.LastIp,
            Country          = _existing?.Country,
            City             = _existing?.City,
            Health           = _existing?.Health ?? ProxyHealth.Unknown,
            LastCheckedAt    = _existing?.LastCheckedAt,
            CreatedAt        = _existing?.CreatedAt ?? default,
            UpdatedAt        = default,
        };

        AutoTestAfterSave = AutoTestField.IsChecked == true;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowError(string msg)
    {
        ErrorText.Text       = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private static string? NullIfBlank(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Compact slug: lowercase, alnum + dashes, max 40 chars.
    /// Returns null if input is empty or distills to nothing.
    /// </summary>
    private static string? Slugify(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var s = Regex.Replace(input.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-");
        s = s.Trim('-');
        if (s.Length == 0) return null;
        if (s.Length > 40) s = s[..40].TrimEnd('-');
        return s;
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using GhostShell.Core.Models;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Code-behind for the Bulk-Create Profiles modal. Mirrors the
/// legacy web's <c>_openBulkCreateModal</c> behaviour:
///
///   • Live-updating preview of the planned name range in the footer
///   • Client-side validation (count 1–100, prefix charset, start ≥ 1)
///   • Proxy multi-select populated from <see cref="IProxyService"/>
///   • Submit fires <see cref="IProfileService.BulkCreateAsync"/> and
///     reports the created/skipped split inline before closing.
///
/// The dialog stays open while the bulk-create is in flight so the
/// user sees the status panel update; it auto-closes on full success
/// after a short pause, but on partial failure waits for the user
/// to dismiss.
/// </summary>
public partial class BulkCreateProfilesDialog : Window
{
    private static readonly Regex PrefixRegex = new(
        @"^[A-Za-z0-9_\-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IProfileService _profiles;
    private readonly IProxyService _proxies;

    /// <summary>
    /// True after a successful bulk-create. The view-model checks this
    /// to decide whether to refresh its list.
    /// </summary>
    public bool DidCreate { get; private set; }

    public BulkCreateProfilesDialog(IProfileService profiles, IProxyService proxies)
    {
        InitializeComponent();
        _profiles = profiles;
        _proxies  = proxies;

        // Live preview when prefix / start / count change. We use
        // PreviewKeyUp via TextChanged on TextBoxes so each keystroke
        // updates the footer line — same vibe as the legacy modal's
        // updateRangeHint().
        PrefixField.TextChanged += (_, _) => RefreshPreview();
        CountField.TextChanged  += (_, _) => RefreshPreview();
        StartField.TextChanged  += (_, _) => RefreshPreview();

        Loaded += async (_, _) =>
        {
            await PopulateProxiesAsync();
            RefreshPreview();
            PrefixField.Focus();
            PrefixField.SelectAll();
        };
    }

    private async Task PopulateProxiesAsync()
    {
        try
        {
            var rows = await _proxies.ListAsync();
            // IpType is an enum (Unknown / Datacenter / Residential /
            // …), not a string — `??` doesn't apply. Render the enum
            // name verbatim and only fall back when it's the Unknown
            // sentinel.
            ProxyPoolList.ItemsSource = rows
                .Select(p => new ProxyPickerRow(
                    p.Slug,
                    $"· {p.Country ?? "?"} · {(p.IpType == IpType.Unknown ? "?" : p.IpType.ToString().ToLowerInvariant())}"))
                .ToList();
        }
        catch
        {
            // Proxies are optional; an empty list is a valid
            // outcome and shouldn't block bulk-create.
            ProxyPoolList.ItemsSource = new List<ProxyPickerRow>();
        }
    }

    private void RefreshPreview()
    {
        var prefix = PrefixField.Text?.Trim() ?? "";
        if (!int.TryParse(CountField.Text, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var count))
            count = 0;
        if (!int.TryParse(StartField.Text, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var start))
            start = 1;

        if (count <= 0 || string.IsNullOrEmpty(prefix))
        {
            PreviewText.Text = "Set a prefix and a count to preview the range";
            return;
        }

        var first = $"{prefix}{start:000}";
        var last  = $"{prefix}{start + count - 1:000}";
        PreviewText.Text = count == 1
            ? $"Will create: {first}"
            : $"Will create: {first} … {last}";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        // ─── Client-side validation ───
        var prefix = (PrefixField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(prefix) || !PrefixRegex.IsMatch(prefix))
        {
            ShowStatus("Prefix is required. Letters, digits, _ and - only.", isError: true);
            return;
        }
        if (!int.TryParse(CountField.Text, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var count)
            || count is < 1 or > 100)
        {
            ShowStatus("Count must be between 1 and 100.", isError: true);
            return;
        }
        if (!int.TryParse(StartField.Text, NumberStyles.Integer,
                          CultureInfo.InvariantCulture, out var start)
            || start < 1)
        {
            ShowStatus("Start index must be 1 or higher.", isError: true);
            return;
        }

        // Disable the button + show "in flight" status so a slow
        // BulkCreateAsync (hundreds of inserts under a transaction)
        // doesn't leave the user wondering if anything is happening.
        CreateBtn.IsEnabled = false;
        ShowStatus($"Creating {count} profile(s)…", isError: false);

        try
        {
            var pool = ProxyPoolList.SelectedItems
                .OfType<ProxyPickerRow>()
                .Select(r => r.Slug)
                .ToList();

            var template = (TemplateField.SelectedItem is ComboBoxItem cbi
                            && cbi.Tag is string tag && !string.IsNullOrEmpty(tag))
                ? tag : null;
            var language = (LanguageField.Text ?? "").Trim();
            if (string.IsNullOrEmpty(language)) language = null!;

            var req = new BulkCreateProfilesRequest(
                Prefix:           prefix,
                Count:            count,
                StartIndex:       start,
                Language:         language,
                TemplateId:       template,
                ProxyPool:        pool,
                EnrichOnFirstRun: EnrichCheckbox.IsChecked == true);

            var result = await _profiles.BulkCreateAsync(req);
            DidCreate = result.Created.Count > 0;

            if (result.Skipped.Count == 0)
            {
                ShowStatus(
                    $"✓ Created {result.Created.Count} profile(s). Closing…",
                    isError: false);
                // Brief pause so the user sees the success message,
                // then close.
                await Task.Delay(700);
                DialogResult = true;
                Close();
            }
            else
            {
                ShowStatus(
                    $"Created {result.Created.Count} profile(s). " +
                    $"Skipped {result.Skipped.Count} (name already exists): " +
                    string.Join(", ", result.Skipped.Take(8)) +
                    (result.Skipped.Count > 8 ? $", +{result.Skipped.Count - 8} more" : ""),
                    isError: false);
                CreateBtn.Content = "Done";
                CreateBtn.Click -= OnCreate;
                CreateBtn.Click += (_, _) => { DialogResult = true; Close(); };
                CreateBtn.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            ShowStatus($"Bulk create failed: {ex.Message}", isError: true);
            CreateBtn.IsEnabled = true;
        }
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusBox.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusBox.BorderBrush = isError
            ? (System.Windows.Media.Brush)Application.Current.Resources["ErrBrush"]
            : (System.Windows.Media.Brush)Application.Current.Resources["Border"];
    }

    private sealed record ProxyPickerRow(string Slug, string Subtitle);
}

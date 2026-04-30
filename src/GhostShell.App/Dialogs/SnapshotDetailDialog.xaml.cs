// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using System.Windows.Controls;
using GhostShell.App.ViewModels;
using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Read-only inspector for a single snapshot. Renders cookies as a
/// DataGrid with filter-by-domain / filter-by-name. Storage entries
/// (per-origin localStorage / sessionStorage) are summarised in the
/// footer — full storage drilldown is a Phase-8 candidate (it's
/// rarely useful in practice; cookies do 95% of the work).
/// </summary>
public partial class SnapshotDetailDialog : Window
{
    private readonly List<CookieRowView> _all;

    public SnapshotDetailDialog(SessionSnapshot meta, SessionPayload payload)
    {
        InitializeComponent();

        TitleText.Text    = $"Snapshot #{meta.Id} — {meta.ProfileName}";
        SubtitleText.Text = $"{meta.CreatedAt:yyyy-MM-dd HH:mm:ss}  ·  trigger={meta.Trigger}" +
                            (string.IsNullOrEmpty(meta.Reason) ? "" : $"  ·  {meta.Reason}");

        _all = payload.Cookies
            .OrderBy(c => c.Domain.TrimStart('.'), StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(CookieRowView.From)
            .ToList();
        CookieGrid.ItemsSource = _all;
        StatsText.Text = $"{_all.Count} cookies · {meta.DomainCount} domains";

        var origins = payload.Storage.Count;
        var keys = payload.Storage.Sum(s => s.LocalStorage.Count + s.SessionStorage.Count);
        StorageStatsText.Text = origins > 0
            ? $"Storage: {keys} item(s) across {origins} origin(s)"
            : "Storage: empty";
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var raw = FilterField.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(raw))
        {
            CookieGrid.ItemsSource = _all;
            StatsText.Text = $"{_all.Count} cookies";
            return;
        }
        var filtered = _all.Where(r =>
            r.Domain.Contains(raw, StringComparison.OrdinalIgnoreCase)
            || r.Name.Contains(raw, StringComparison.OrdinalIgnoreCase))
            .ToList();
        CookieGrid.ItemsSource = filtered;
        StatsText.Text = $"{filtered.Count} of {_all.Count} cookies";
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}

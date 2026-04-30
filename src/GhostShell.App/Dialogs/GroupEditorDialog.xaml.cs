// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using GhostShell.Core.Models;
using GhostShell.Core.Services;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Code-behind for the Create / Edit Group modal. Renders a name +
/// description + max-parallel + members-checkbox-list shape that
/// matches the legacy web's edit-group dialog.
///
/// Member picker is filterable via the search box at the top of
/// the list; the selected count flows into the header so the user
/// always sees how many they've ticked.
/// </summary>
public partial class GroupEditorDialog : Window
{
    private readonly IProfileGroupService _service;
    private readonly ProfileGroup? _existing;
    private readonly List<MemberPick> _allMembers;
    private readonly ObservableCollection<MemberPick> _visibleMembers = new();

    /// <summary>True after a successful save so the parent can
    /// trigger a reload.</summary>
    public bool DidSave { get; private set; }

    public GroupEditorDialog(
        IProfileGroupService service,
        ProfileGroup? existing,
        IReadOnlyList<Profile> allProfiles)
    {
        InitializeComponent();
        _service  = service;
        _existing = existing;

        // Build the picker once. Each row tracks IsSelected via the
        // observable wrapper so per-row checkbox toggling auto-updates
        // the header counter.
        var preselected = existing?.Members?.ToHashSet(StringComparer.OrdinalIgnoreCase)
                          ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _allMembers = allProfiles
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new MemberPick
            {
                Name       = p.Name,
                Subtitle   = $"· {p.TemplateId ?? "auto"} · {p.ProxySlug ?? "no proxy"}",
                IsSelected = preselected.Contains(p.Name),
            })
            .ToList();
        foreach (var m in _allMembers)
        {
            m.PropertyChanged += OnMemberPropertyChanged;
            _visibleMembers.Add(m);
        }
        MembersList.ItemsSource = _visibleMembers;

        if (existing is not null)
        {
            TitleText.Text = $"✏ Edit group: {existing.Name}";
            NameField.Text = existing.Name;
            DescriptionField.Text = existing.Description ?? "";
            MaxParallelField.Text = existing.MaxParallel?.ToString(CultureInfo.InvariantCulture) ?? "";
        }

        UpdateMemberCount();
        Loaded += (_, _) =>
        {
            NameField.Focus();
            NameField.SelectAll();
        };
    }

    private void OnMemberPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemberPick.IsSelected))
            UpdateMemberCount();
    }

    private void UpdateMemberCount()
        => MemberCountText.Text = _allMembers.Count(m => m.IsSelected).ToString();

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        var needle = ProfileFilterField.Text?.Trim() ?? "";
        _visibleMembers.Clear();
        foreach (var m in _allMembers)
        {
            if (string.IsNullOrEmpty(needle)
                || m.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                _visibleMembers.Add(m);
            }
        }
    }

    /// <summary>
    /// Click handler on the row's checkbox so a click anywhere on
    /// the row (not just the tiny checkbox glyph) toggles the
    /// selection. The CheckBox carries the IsChecked binding via
    /// MemberPick.IsSelected, so this handler is mostly a no-op
    /// — but we keep it as a hook for future "shift-click range
    /// select" if we add that.
    /// </summary>
    private void OnMemberToggled(object sender, RoutedEventArgs e)
        => UpdateMemberCount();

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var name = (NameField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowStatus("Name is required.", isError: true);
            return;
        }

        int? cap = null;
        var capText = (MaxParallelField.Text ?? "").Trim();
        if (!string.IsNullOrEmpty(capText))
        {
            if (!int.TryParse(capText, NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out var capVal)
                || capVal < 1 || capVal > 100)
            {
                ShowStatus("Max parallel must be 1–100, or empty.", isError: true);
                return;
            }
            cap = capVal;
        }

        var description = (DescriptionField.Text ?? "").Trim();
        if (string.IsNullOrEmpty(description)) description = null!;

        var members = _allMembers.Where(m => m.IsSelected).Select(m => m.Name).ToList();

        SaveBtn.IsEnabled = false;
        ShowStatus("Saving…", isError: false);

        try
        {
            if (_existing is null)
            {
                await _service.CreateAsync(name, description, cap, members);
            }
            else
            {
                await _service.UpdateAsync(_existing.Id, name, description, cap);
                await _service.SetMembersAsync(_existing.Id, members);
            }
            DidSave = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            ShowStatus($"Save failed: {ex.Message}", isError: true);
            SaveBtn.IsEnabled = true;
        }
    }

    private void ShowStatus(string text, bool isError)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError
            ? (System.Windows.Media.Brush)Application.Current.Resources["ErrBrush"]
            : (System.Windows.Media.Brush)Application.Current.Resources["TextMuted"];
    }

    private sealed partial class MemberPick : ObservableObject
    {
        public required string Name { get; init; }
        public required string Subtitle { get; init; }
        [ObservableProperty] private bool _isSelected;
    }
}

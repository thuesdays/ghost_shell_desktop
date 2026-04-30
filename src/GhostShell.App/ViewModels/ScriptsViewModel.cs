// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Scripts library page (Phase 12 iter 6).
///
/// Surface: list / create / edit (JSON) / delete / import (file) /
/// export (file) / set-default / apply-to-profiles. Drag-drop graph
/// editor remains Phase 13.
/// </summary>
public sealed partial class ScriptsViewModel : BaseViewModel
{
    private readonly IScriptService _service;
    private readonly IProfileService _profiles;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ScriptsViewModel> _log;

    public ScriptsViewModel(
        IScriptService service,
        IProfileService profiles,
        IDialogService dialogs,
        ILogger<ScriptsViewModel> log)
    {
        _service  = service;
        _profiles = profiles;
        _dialogs  = dialogs;
        _log      = log;
    }

    /// <summary>All cards (post-load), unfiltered.</summary>
    private readonly List<ScriptCardVm> _allCards = new();

    /// <summary>Visible cards after search/filter.</summary>
    public ObservableCollection<ScriptCardVm> Items { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>Free-text search across name + description.</summary>
    [ObservableProperty] private string? _searchText;

    partial void OnSearchTextChanged(string? value) => ApplyFilter();

    public override async Task OnNavigatedToAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var list = await _service.ListAsync();
            // Pre-compute per-script profile-assignment count once
            // here, instead of per-card on every render. The N is
            // small (≤ a few hundred profiles); a single ListAsync
            // + group-by is fine.
            var profiles = await _profiles.ListAsync();
            var assignmentCounts = profiles
                .Where(p => p.AssignedScriptId is not null)
                .GroupBy(p => p.AssignedScriptId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());

            _allCards.Clear();
            foreach (var s in list)
            {
                assignmentCounts.TryGetValue(s.Id, out var profCount);
                _allCards.Add(ScriptCardVm.From(s, profCount));
            }
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scripts list reload failed");
        }
        finally { IsBusy = false; }
    }

    private void ApplyFilter()
    {
        IEnumerable<ScriptCardVm> q = _allCards;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            q = q.Where(c =>
                c.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (c.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        Items.Clear();
        foreach (var c in q.OrderByDescending(c => c.IsDefault)
                            .ThenByDescending(c => c.UpdatedAt))
            Items.Add(c);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task OpenTemplatesAsync()
    {
        // Templates library is Phase 16 — for now we surface the
        // intent so the button is honest.
        await _dialogs.ConfirmAsync(
            "Templates — coming soon",
            "Curated script templates (commercial-inflate, LinkedIn login, " +
            "ad-density warmup, etc.) ship in Phase 16. For now use " +
            "Import to load a JSON file you authored or got from a teammate.",
            "OK", ConfirmSeverity.Info);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var (saved, etag) = await OpenEditorAsync(existing: null);
        if (saved is null) return;
        try
        {
            var created = await _service.CreateAsync(saved);
            if (saved.IsDefault)
                await _service.SetDefaultAsync(created.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script create failed");
            await _dialogs.ConfirmAsync("Create failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    /// <summary>
    /// Open the visual editor; if the user clicks "JSON view" inside,
    /// fall back to the raw-JSON dialog. Returns (script-or-null,
    /// expected-etag) — null script means cancelled.
    /// </summary>
    private async Task<(Script? Saved, string? ExpectedEtag)> OpenEditorAsync(Script? existing)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var visual = new ScriptVisualEditorDialog(existing) { Owner = owner };
        var visualResult = visual.ShowDialog();
        if (visualResult == true && visual.Result is not null)
            return (visual.Result, visual.ResultExpectedEtag);
        if (visual.SwitchToJson)
        {
            // Round-trip the user's name/desc/flags through the JSON
            // dialog so they don't lose what they typed before the
            // switch. existing is the still-correct snapshot.
            var json = new ScriptJsonEditorDialog(existing) { Owner = owner };
            if (json.ShowDialog() == true && json.Result is not null)
                return (json.Result, json.ResultExpectedEtag);
        }
        await Task.CompletedTask;
        return (null, null);
    }

    [RelayCommand]
    private async Task EditAsync(ScriptCardVm? card)
    {
        if (card is null) return;
        var selected = await _service.GetAsync(card.Id);
        if (selected is null) return;
        var (saved, etag) = await OpenEditorAsync(selected);
        if (saved is null) return;
        try
        {
            await _service.UpdateAsync(saved, etag ?? "");
            // SetDefault toggle is independent of ETag — it can race
            // safely (the underlying SetDefaultAsync clears ALL then
            // sets one inside a transaction).
            if (saved.IsDefault && !selected.IsDefault)
                await _service.SetDefaultAsync(selected.Id);
            else if (!saved.IsDefault && selected.IsDefault)
                await _service.SetDefaultAsync(0);
            await ReloadAsync();
        }
        catch (InvalidOperationException ex) // ETag conflict
        {
            await _dialogs.ConfirmAsync("Save conflict", ex.Message,
                "OK", ConfirmSeverity.Warning);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script update failed");
            await _dialogs.ConfirmAsync("Update failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ScriptCardVm? card)
    {
        if (card is null) return;
        var ok = await _dialogs.ConfirmAsync(
            $"Delete '{card.Name}'?",
            $"Script #{card.Id} will be removed. Run history rows remain.",
            "Delete", ConfirmSeverity.Danger);
        if (!ok) return;
        try
        {
            await _service.DeleteAsync(card.Id);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script delete failed");
            await _dialogs.ConfirmAsync("Delete failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import script from JSON",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var raw = await File.ReadAllTextAsync(dlg.FileName);
            // Two valid input shapes: a bare steps-array, or a
            // wrapped {name, description, steps: [...]} object. The
            // legacy export emits the wrapped form so we round-trip
            // user data correctly.
            string name, description, stepsJson;
            using (var doc = JsonDocument.Parse(raw))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    name = Path.GetFileNameWithoutExtension(dlg.FileName);
                    description = "Imported from " + dlg.FileName;
                    stepsJson = JsonSerializer.Serialize(doc.RootElement);
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    name = doc.RootElement.TryGetProperty("name", out var n)
                                ? n.GetString() ?? "imported" : "imported";
                    description = doc.RootElement.TryGetProperty("description", out var d)
                                ? d.GetString() ?? "" : "";
                    if (!doc.RootElement.TryGetProperty("steps", out var steps))
                        throw new InvalidOperationException("missing 'steps' array");
                    stepsJson = JsonSerializer.Serialize(steps);
                }
                else
                {
                    throw new InvalidOperationException("expected JSON array or object");
                }
            }
            await _service.CreateAsync(new Script
            {
                Name = UniqueName(name),
                Description = description,
                StepsJson = stepsJson,
                Enabled = true,
            });
            await ReloadAsync();
            await _dialogs.ConfirmAsync("Imported",
                $"Script '{name}' imported.", "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Import failed");
            await _dialogs.ConfirmAsync("Import failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ExportAsync(ScriptCardVm? card)
    {
        if (card is null) return;
        var selected = await _service.GetAsync(card.Id);
        if (selected is null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Export script to JSON",
            FileName = $"{Sanitise(selected.Name)}.json",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // Wrapped form so name + description are preserved on
            // round-trip via Import.
            var wrapped = new Dictionary<string, object?>
            {
                ["name"]        = selected.Name,
                ["description"] = selected.Description,
                ["steps"]       = JsonDocument.Parse(selected.StepsJson).RootElement,
            };
            var json = JsonSerializer.Serialize(wrapped, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            await File.WriteAllTextAsync(dlg.FileName, json);
            await _dialogs.ConfirmAsync("Exported",
                $"Wrote '{selected.Name}' to:\n{dlg.FileName}",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Export failed");
            await _dialogs.ConfirmAsync("Export failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task SetAsDefaultAsync(ScriptCardVm? card)
    {
        if (card is null) return;
        try
        {
            // Toggle: clicking the action on the already-default row
            // clears the default. Otherwise sets it to this row.
            var newDefault = card.IsDefault ? 0L : card.Id;
            await _service.SetDefaultAsync(newDefault);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Set default failed");
            await _dialogs.ConfirmAsync("Failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ApplyToProfilesAsync(ScriptCardVm? card)
    {
        if (card is null) return;
        var selected = await _service.GetAsync(card.Id);
        if (selected is null) return;
        try
        {
            var profiles = await _profiles.ListAsync();
            if (profiles.Count == 0)
            {
                await _dialogs.ConfirmAsync(
                    "No profiles",
                    "Create profiles first — there's nothing to assign this script to.",
                    "OK", ConfirmSeverity.Warning);
                return;
            }

            // Phase 13D: real multi-select dialog. Pre-check rows that
            // already have this script assigned so the user can see the
            // current state before applying changes.
            var preChecked = profiles
                .Where(p => p.AssignedScriptId == selected.Id)
                .Select(p => p.Name);
            var dlg = new Dialogs.ProfilePickerDialog(
                title: $"Apply '{selected.Name}' to profiles",
                subtitle: "Tick the profiles that should run this script on launch.",
                profileNames: profiles.Select(p => p.Name),
                preChecked: preChecked)
            {
                Owner = System.Windows.Application.Current?.MainWindow,
            };
            if (dlg.ShowDialog() != true) return;
            if (dlg.SelectedNames.Count == 0) return;
            await _service.AssignToProfilesAsync(selected.Id, dlg.SelectedNames);
            await _dialogs.ConfirmAsync(
                "Done",
                $"Assigned '{selected.Name}' to {dlg.SelectedNames.Count} profile(s).",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Apply-to-profiles failed");
            await _dialogs.ConfirmAsync("Failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    private string UniqueName(string baseName)
    {
        // Avoid the UNIQUE-constraint hit on import by suffixing.
        // We compare against _allCards (the unfiltered set) so that
        // an active filter doesn't trick us into a name that's
        // hidden but still in the DB.
        bool Taken(string n) => _allCards.Any(c =>
            string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase));
        if (!Taken(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName}_{i}";
            if (!Taken(candidate)) return candidate;
        }
        return baseName + "_x";
    }

    private static string Sanitise(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray());
    }
}

/// <summary>
/// Card-shaped projection of a <see cref="Script"/> with the
/// pre-computed display fields (StepCount, AccentBrushKey, etc.) the
/// Scripts page binds to. Built once per reload — recomputing per
/// render would parse the steps_json on every frame.
/// </summary>
public sealed record ScriptCardVm
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required bool Enabled { get; init; }
    public required bool IsDefault { get; init; }
    public DateTime UpdatedAt { get; init; }

    public required int StepCount { get; init; }
    public required int ProfilesAssigned { get; init; }

    /// <summary>Per-script hue derived from the name hash. Same
    /// approach as the Profiles cards — gives the grid a stable
    /// colour map without storing colours in the DB.</summary>
    public required string AccentBrushKey { get; init; }

    /// <summary>"6d ago" / "just now" / "2026-04-30".</summary>
    public string UpdatedAgo
    {
        get
        {
            if (UpdatedAt == default) return "—";
            var utc = UpdatedAt.Kind switch
            {
                DateTimeKind.Utc          => UpdatedAt,
                DateTimeKind.Local        => UpdatedAt.ToUniversalTime(),
                _                         => DateTime.SpecifyKind(UpdatedAt, DateTimeKind.Utc),
            };
            var d = DateTime.UtcNow - utc;
            if (d.TotalSeconds < 30)  return "just now";
            if (d.TotalMinutes < 60)  return $"{(int)d.TotalMinutes}m ago";
            if (d.TotalHours   < 24)  return $"{(int)d.TotalHours}h ago";
            if (d.TotalDays    < 30)  return $"{(int)d.TotalDays}d ago";
            return UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    public string StepsLabel    => StepCount == 1 ? "1 step"     : $"{StepCount} steps";
    public string ProfilesLabel => ProfilesAssigned == 1 ? "1 profile" : $"{ProfilesAssigned} profiles";

    public static ScriptCardVm From(Script s, int profilesAssigned)
    {
        var stepCount = CountSteps(s.StepsJson);
        var hash = (uint)(s.Name?.GetHashCode() ?? 0);
        var hue = (hash % 5) switch
        {
            0 => "HueBlue",
            1 => "HueGreen",
            2 => "HueViolet",
            3 => "HueAmber",
            _ => "HueTeal",
        };
        return new ScriptCardVm
        {
            Id               = s.Id,
            Name             = s.Name,
            Description      = string.IsNullOrWhiteSpace(s.Description) ? "(no description)" : s.Description,
            Enabled          = s.Enabled,
            IsDefault        = s.IsDefault,
            UpdatedAt        = s.UpdatedAt,
            StepCount        = stepCount,
            ProfilesAssigned = profilesAssigned,
            AccentBrushKey   = hue,
        };
    }

    /// <summary>
    /// Cheap shallow step-count: count top-level array entries, no
    /// recursion into nested then/else/body. Anything more expensive
    /// would mean parsing steps_json on every reload — fine when
    /// scripts are short, expensive when they're not.
    /// </summary>
    private static int CountSteps(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json ?? "[]");
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.GetArrayLength() : 0;
        }
        catch { return 0; }
    }
}

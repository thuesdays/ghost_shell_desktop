// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;        // Phase 66 — Application.Current for Dispatcher marshal in RecordAsync
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.App.Navigation;
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
    private readonly IProfileRunner _runner;
    private readonly IDialogService _dialogs;
    // Phase 34 — INavigationService is wired so the "Domain lists"
    // toolbar button can route to the (un-sidebar-listed) domains
    // page. The page+VM+route are still registered in App; only the
    // sidebar entry was removed.
    private readonly INavigationService _nav;
    private readonly ILogger<ScriptsViewModel> _log;

    public ScriptsViewModel(
        IScriptService service,
        IProfileService profiles,
        IProfileRunner  runner,
        IDialogService dialogs,
        INavigationService nav,
        ILogger<ScriptsViewModel> log)
    {
        _service  = service;
        _profiles = profiles;
        _runner   = runner;
        _dialogs  = dialogs;
        _nav      = nav;
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

    /// <summary>Phase 34 — toolbar button on the Scripts page that
    /// routes to the Domains page. The Domains page is not in the
    /// sidebar anymore; this is the only entry point.</summary>
    [RelayCommand]
    private void OpenDomainLists() => _nav.NavigateTo("domains");

    [RelayCommand]
    private async Task OpenTemplatesAsync()
    {
        // Phase 23: real templates gallery. Pick one → seed a new
        // Script with the template's StepsJson + name/description,
        // then drop it straight into the editor for further tweaking.
        var owner = System.Windows.Application.Current?.MainWindow;
        var dlg = new ScriptTemplatesDialog { Owner = owner };
        if (dlg.ShowDialog() != true || dlg.Selected is null) return;

        var t = dlg.Selected;
        // Make the seed name unique among existing scripts so the
        // user lands in the editor without an immediate name clash.
        var existingNames = _allCards
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seedName = UniqueName(t.Name, existingNames);

        var seed = new Script
        {
            Id          = 0,
            Name        = seedName,
            Description = t.Description,
            StepsJson   = t.StepsJson,
            Enabled     = true,
            IsDefault   = false,
            ETag        = "",
            CreatedAt   = default,
            UpdatedAt   = default,
            LayoutMode  = "list",
        };

        // Open the editor pre-populated. Save flow goes through the
        // same path as a regular CreateAsync.
        var (saved, _, runAfter) = await OpenEditorAsync(existing: seed);
        if (saved is null) return;
        try
        {
            var created = await _service.CreateAsync(saved);
            if (saved.IsDefault)
                await _service.SetDefaultAsync(created.Id);
            await ReloadAsync();
            if (runAfter)
                await KickRunOnPickedProfilesAsync(created.Id, created.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Template-based create failed");
            await _dialogs.ConfirmAsync("Create failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    /// <summary>De-dupe a name against the current scripts list by
    /// appending " (2)", " (3)", … until it's unique.</summary>
    private static string UniqueName(string baseName, HashSet<string> taken)
    {
        if (!taken.Contains(baseName)) return baseName;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return baseName + " (copy)";
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        // OpenEditorAsync returns a 3-tuple now (Phase 20 added the
        // run-after-save flag). For Create, etag and runAfter aren't
        // meaningful — drop both with discards.
        var (saved, _, _) = await OpenEditorAsync(existing: null);
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
    /// Phase 63 — Record a new script by interacting with a live
    /// browser session. Opens <see cref="GhostShell.App.Dialogs.ScriptRecorderDialog"/>
    /// which handles profile launch + recorder lifecycle + script
    /// creation. On success the new script lands in the list.
    /// </summary>
    [RelayCommand]
    private async Task RecordAsync()
    {
        try
        {
            var recorded = await _dialogs.ShowScriptRecorderAsync();
            if (recorded is null)
            {
                _log.LogDebug("Recording cancelled or failed");
                return;
            }
            _log.LogInformation(
                "Recorded script saved (id={Id}, name='{Name}') — refreshing list",
                recorded.Id, recorded.Name);

            // Phase 66 — belt-and-braces refresh after save. Earlier
            // builds called only `await ReloadAsync()` here and the
            // user reported the new script not appearing until they
            // navigated away and back. Two layers now:
            //   1. Direct in-place insert of the new ScriptCardVm so
            //      the user sees the row IMMEDIATELY without waiting
            //      for any DB round-trip.
            //   2. Full ReloadAsync afterwards (marshalled to the UI
            //      thread) to pick up assignment counts and the
            //      definitive sort order.
            try
            {
                var profiles = await _profiles.ListAsync();
                var assignedCount = profiles.Count(p =>
                    p.AssignedScriptId == recorded.Id);
                var card = ScriptCardVm.From(recorded, assignedCount);
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    // Skip if already present (defensive — shouldn't happen
                    // because CreateAsync just minted a new id).
                    if (_allCards.All(c => c.Id != card.Id))
                    {
                        _allCards.Insert(0, card);
                        ApplyFilter();
                    }
                });
            }
            catch (Exception fastEx)
            {
                _log.LogWarning(fastEx, "Recorder fast-path insert failed; full reload will follow");
            }

            // Full reload to reconcile any state we missed.
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await ReloadAsync();
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recorder dialog crashed");
            await _dialogs.ConfirmAsync(
                "Recording failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    /// <summary>
    /// Open the right editor for the script's layout mode. Existing
    /// list-mode scripts open in <see cref="ScriptVisualEditorDialog"/>;
    /// graph-mode scripts open in <see cref="ScriptGraphEditorDialog"/>.
    /// JSON view is always available as a fallback.
    /// Returns (script-or-null, expected-etag, run-after-save).
    /// </summary>
    private async Task<(Script? Saved, string? ExpectedEtag, bool RunAfterSave)>
        OpenEditorAsync(Script? existing)
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        var isGraph = existing is not null
            && string.Equals(existing.LayoutMode, "graph", StringComparison.OrdinalIgnoreCase);

        if (isGraph)
        {
            var graph = new ScriptGraphEditorDialog(existing!) { Owner = owner };
            var gr = graph.ShowDialog();
            if (gr == true && graph.Result is not null)
                return (graph.Result, graph.ResultExpectedEtag, false);
            if (graph.SwitchToJson)
            {
                var json = new ScriptJsonEditorDialog(existing) { Owner = owner };
                if (json.ShowDialog() == true && json.Result is not null)
                    return (json.Result, json.ResultExpectedEtag, false);
            }
            await Task.CompletedTask;
            return (null, null, false);
        }

        var visual = new ScriptVisualEditorDialog(existing) { Owner = owner };
        var visualResult = visual.ShowDialog();
        if (visualResult == true && visual.Result is not null)
            return (visual.Result, visual.ResultExpectedEtag, visual.RequestRun);
        if (visual.SwitchToGraph)
        {
            // List → graph conversion. The list-editor snapshots the
            // user's typed state into visual.Result before raising
            // SwitchToGraph, so we always have *something* to seed
            // the converter — even for brand-new (existing=null) scripts.
            var seed = visual.Result
                ?? existing
                ?? new Script { Name = "new_script", LayoutMode = "list", StepsJson = "[]" };
            try
            {
                _log.LogInformation(
                    "Switching to graph mode (seed: name='{Name}', steps_len={Len}, existing_id={Id})",
                    seed.Name, seed.StepsJson?.Length ?? 0, existing?.Id ?? 0);
                var converted = ConvertListToGraph(seed, seed);
                var graph = new ScriptGraphEditorDialog(converted) { Owner = owner };
                var gr = graph.ShowDialog();
                if (gr == true && graph.Result is not null)
                    return (graph.Result, graph.ResultExpectedEtag, false);
            }
            catch (Exception ex)
            {
                // Without this catch, any XAML/resource/runtime issue
                // in the graph dialog returns silently and the user
                // sees the Scripts page with no clue why. Surface it.
                _log.LogError(ex, "Failed to open graph editor");
                await _dialogs.ConfirmAsync(
                    "Graph editor failed",
                    "Couldn't open the graph editor:\n\n" + ex.Message
                        + "\n\nCheck Logs for details. Falling back to list mode.",
                    "OK", ConfirmSeverity.Error);
            }
        }
        if (visual.SwitchToJson)
        {
            // Round-trip the user's name/desc/flags through the JSON
            // dialog so they don't lose what they typed before the
            // switch. existing is the still-correct snapshot.
            var json = new ScriptJsonEditorDialog(existing) { Owner = owner };
            if (json.ShowDialog() == true && json.Result is not null)
                return (json.Result, json.ResultExpectedEtag, false);
        }
        await Task.CompletedTask;
        return (null, null, false);
    }

    /// <summary>
    /// Convert a list-mode script into graph mode by emitting one
    /// node per step laid out in a single vertical column, with
    /// straight chained edges (n1 → n2 → … → nN). Each step's params
    /// + per-step flags are preserved verbatim. Branches inside if /
    /// foreach steps are NOT expanded (graph-mode if uses then/else
    /// labelled edges instead). The user can rewire post-conversion.
    /// </summary>
    private static Script ConvertListToGraph(Script existing, Script latest)
    {
        try
        {
            using var doc = JsonDocument.Parse(latest.StepsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return existing with { LayoutMode = "graph", NodesJson = "[]", EdgesJson = "[]" };

            var nodes = new List<JsonElement>();
            foreach (var s in doc.RootElement.EnumerateArray()) nodes.Add(s.Clone());

            using var nMs = new System.IO.MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(nMs))
            {
                w.WriteStartArray();
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    w.WriteStartObject();
                    w.WriteString("id", "n" + (i + 1));
                    foreach (var prop in node.EnumerateObject())
                        prop.WriteTo(w);
                    w.WriteNumber("x", 200);
                    w.WriteNumber("y", 80 + i * 130);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            using var eMs = new System.IO.MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(eMs))
            {
                w.WriteStartArray();
                for (var i = 0; i < nodes.Count - 1; i++)
                {
                    w.WriteStartObject();
                    w.WriteString("from", "n" + (i + 1));
                    w.WriteString("to",   "n" + (i + 2));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            return existing with
            {
                LayoutMode = "graph",
                NodesJson  = System.Text.Encoding.UTF8.GetString(nMs.ToArray()),
                EdgesJson  = System.Text.Encoding.UTF8.GetString(eMs.ToArray()),
            };
        }
        catch
        {
            return existing with { LayoutMode = "graph", NodesJson = "[]", EdgesJson = "[]" };
        }
    }

    [RelayCommand]
    private async Task EditAsync(ScriptCardVm? card)
    {
        if (card is null) return;
        var selected = await _service.GetAsync(card.Id);
        if (selected is null) return;
        var (saved, etag, runAfter) = await OpenEditorAsync(selected);
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
            if (runAfter)
                await KickRunOnPickedProfilesAsync(saved.Id, saved.Name);
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

    /// <summary>
    /// Phase 20 helper used by the editor's "▶ Run on…" button. Opens
    /// the multi-select profile picker, temporarily reassigns the
    /// chosen profiles to <paramref name="scriptId"/>, then kicks the
    /// runner. Profiles already running are skipped (the runner
    /// rejects double-start). Errors are surfaced via the dialog
    /// service rather than crashing the VM.
    /// </summary>
    private async Task KickRunOnPickedProfilesAsync(long scriptId, string scriptName)
    {
        var profiles = await _profiles.ListAsync();
        if (profiles.Count == 0)
        {
            await _dialogs.ConfirmAsync("No profiles",
                "Create a profile first — there's nothing to run the script against.",
                "OK", ConfirmSeverity.Info);
            return;
        }

        var owner = System.Windows.Application.Current?.MainWindow;
        var preChecked = profiles
            .Where(p => p.AssignedScriptId == scriptId)
            .Select(p => p.Name);
        var picker = new ProfilePickerDialog(
            $"Run '{scriptName}' on…",
            "Pick profiles to run this script on. Each launches its own Chrome session.",
            profiles.Select(p => p.Name),
            preChecked) { Owner = owner };
        if (picker.ShowDialog() != true || picker.SelectedNames.Count == 0) return;

        // Make sure each picked profile is bound to this script before
        // we start it — RealProfileRunner reads AssignedScriptId off
        // the profile after launch to know what to kick.
        var byName = profiles.ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var name in picker.SelectedNames)
        {
            if (!byName.TryGetValue(name, out var p)) continue;
            if (p.AssignedScriptId != scriptId)
            {
                var rebound = p with { AssignedScriptId = scriptId };
                await _profiles.UpdateAsync(rebound);
            }
        }

        // Kick each — fire-and-forget per profile so the user's UI
        // stays snappy. StartAsync is itself async and returns once
        // the launch completes (or fails). Errors are logged.
        var failed = new List<string>();
        foreach (var name in picker.SelectedNames)
        {
            if (!byName.TryGetValue(name, out var p)) continue;
            try
            {
                var rebound = p.AssignedScriptId == scriptId ? p
                    : p with { AssignedScriptId = scriptId };
                await _runner.StartAsync(rebound);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Run-from-editor failed for profile '{P}'", name);
                failed.Add(name);
            }
        }
        if (failed.Count > 0)
        {
            await _dialogs.ConfirmAsync(
                "Some profiles failed to start",
                $"Couldn't start: {string.Join(", ", failed)}.\nCheck Logs for details.",
                "OK", ConfirmSeverity.Warning);
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

            // Phase 35 — single normalising path for both desktop and
            // legacy ghost_shell_browser (web) exports. The converter
            // handles:
            //   • root key:  flow → steps
            //   • loop step: loop → foreach (item_var → params.var,
            //                items/shuffle → params, steps → body)
            //   • foreach_ad / foreach: web's `steps` → desktop `body`
            //   • if step:   then_steps/else_steps → then/else
            //   • leaf params: web inlines them at the top level; the
            //                  converter buckets the unrecognised
            //                  ones under `params` and keeps the
            //                  per-step ad-domain flags at the root
            //   • variables: {name} → {{name}} (the desktop runner's
            //                interpolator wants double braces)
            // The converter is idempotent on already-desktop input,
            // so passing in our own exports is harmless.
            var (stepsJson, normName, normDesc) = ScriptImportConverter.Normalise(raw);

            // Fallbacks: bare arrays don't carry a name; pull a sane
            // default from the filename. Same for description.
            var name = string.IsNullOrWhiteSpace(normName)
                ? Path.GetFileNameWithoutExtension(dlg.FileName)
                : normName;
            var description = string.IsNullOrWhiteSpace(normDesc)
                ? "Imported from " + dlg.FileName
                : normDesc;

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
            // Script.Name is `required string` but Dapper can still
            // hand us null on a malformed row — coalesce defensively
            // so the UI never crashes on a bad record.
            Name             = s.Name ?? "(unnamed)",
            // IsNullOrWhiteSpace=false guarantees Description is
            // non-null, but compiler flow analysis doesn't track that
            // through the helper. Null-forgiving is safe here.
            Description      = string.IsNullOrWhiteSpace(s.Description) ? "(no description)" : s.Description!,
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

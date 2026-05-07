// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

public sealed partial class ProfilesViewModel : BaseViewModel
{
    private readonly IProfileService _profiles;
    private readonly IProfileRunner  _runner;
    private readonly IFingerprintService _fp;
    private readonly IDialogService  _dialogs;
    private readonly ILogger<ProfilesViewModel> _log;

    // Phase 64 — optional services for bulk operations. Optional so
    // existing test wiring without these doesn't break; in production
    // DI provides them.
    private readonly IRunQueueService? _queue;
    private readonly IProxyTester? _proxyTester;
    private readonly IProxyService? _proxyService;

    public ProfilesViewModel(
        IProfileService profiles,
        IProfileRunner runner,
        IFingerprintService fp,
        IDialogService dialogs,
        ILogger<ProfilesViewModel> log,
        IRunQueueService? queue = null,
        IProxyTester? proxyTester = null,
        IProxyService? proxyService = null)
    {
        _profiles     = profiles;
        _runner       = runner;
        _fp           = fp;
        _dialogs      = dialogs;
        _log          = log;
        _queue        = queue;
        _proxyTester  = proxyTester;
        _proxyService = proxyService;

        // Refresh row IsRunning when the runner's active set changes.
        // Marshal to the dispatcher AND skip while a reload is mid-
        // flight — without the IsBusy guard, ActiveChanged could fire
        // between Items.Clear() and the foreach that rebuilds it,
        // and SyncRunningFlags would NRE on a half-mutated collection.
        _runner.ActiveChanged += (_, _) =>
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (IsBusy) return; // ReloadAsync is rebuilding Items
                SyncRunningFlags();
            });
    }

    public ObservableCollection<ProfileRowVm> Items { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;

    /// <summary>
    /// View mode toggle — Cards (legacy web's default) vs Table.
    /// Persisted to ProfilesView preferences via the view's
    /// localStorage-equivalent (Properties.Settings) on change.
    /// </summary>
    [ObservableProperty] private ProfilesViewMode _viewMode = ProfilesViewMode.Cards;
    public bool IsCardsMode => ViewMode == ProfilesViewMode.Cards;
    public bool IsTableMode => ViewMode == ProfilesViewMode.Table;
    partial void OnViewModeChanged(ProfilesViewMode value)
    {
        OnPropertyChanged(nameof(IsCardsMode));
        OnPropertyChanged(nameof(IsTableMode));
    }

    /// <summary>How many rows are currently checked. Drives the
    /// "Bulk start (N)" / "Bulk delete (N)" button labels and
    /// the visibility of the bulk-action strip.</summary>
    [ObservableProperty] private int _selectedCount;
    public bool HasSelection => SelectedCount > 0;
    partial void OnSelectedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(IsAllSelected));
    }

    /// <summary>Phase 71t — header-checkbox tri-state for the
    /// Profiles table. Mirrors VaultViewModel.IsAllSelected:
    ///   true  → every row checked
    ///   false → none checked
    ///   null  → some-but-not-all (indeterminate visual)
    /// Setter propagates to all rows in one pass; the
    /// <see cref="_suppressSelectionBubble"/> guard prevents the
    /// per-row PropertyChanged handler from quadratically recounting
    /// while the loop is mid-flight.</summary>
    private bool _suppressSelectionBubble;
    public bool? IsAllSelected
    {
        get
        {
            if (Items.Count == 0) return false;
            if (SelectedCount == 0) return false;
            if (SelectedCount == Items.Count) return true;
            return null;
        }
        set
        {
            var target = value == true;
            _suppressSelectionBubble = true;
            try
            {
                foreach (var r in Items) r.IsSelected = target;
            }
            finally { _suppressSelectionBubble = false; }
            SelectedCount = Items.Count(i => i.IsSelected);
            OnPropertyChanged(nameof(IsAllSelected));
        }
    }

    public override async Task OnNavigatedToAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _log.LogDebug("Profiles: reload");
        IsBusy = true;
        try
        {
            // Detach checkbox handlers from the old rows so removed
            // VMs don't keep firing into our SelectedCount aggregate.
            foreach (var oldRow in Items) oldRow.PropertyChanged -= OnRowPropertyChanged;

            Items.Clear();
            foreach (var p in await _profiles.ListAsync())
            {
                var row = new ProfileRowVm(p, IsRunning(p.Name));
                row.PropertyChanged += OnRowPropertyChanged;
                Items.Add(row);
            }
            IsEmpty = Items.Count == 0;
            RecountSelected();
            _log.LogInformation("Profiles loaded: {Count} item(s)", Items.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Profiles list failed");
        }
        finally { IsBusy = false; }
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Phase 71t — IsAllSelected setter does the count once after
        // its bulk-set loop; skip the per-row recount while it runs.
        if (_suppressSelectionBubble) return;
        if (e.PropertyName == nameof(ProfileRowVm.IsSelected))
            RecountSelected();
    }

    private void RecountSelected()
        => SelectedCount = Items.Count(r => r.IsSelected);

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var r in Items) r.IsSelected = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var r in Items) r.IsSelected = false;
    }

    [RelayCommand]
    private void SetViewModeCards() => ViewMode = ProfilesViewMode.Cards;

    [RelayCommand]
    private void SetViewModeTable() => ViewMode = ProfilesViewMode.Table;

    /// <summary>Re-evaluate IsRunning on every row without refetching from DB.</summary>
    private void SyncRunningFlags()
    {
        foreach (var row in Items)
            row.IsRunning = IsRunning(row.Profile.Name);
    }

    private bool IsRunning(string name) =>
        _runner.ActiveProfileNames.Contains(name);

    [RelayCommand]
    private async Task AddAsync()
    {
        var draft = await _dialogs.ShowProfileEditorAsync(null);
        if (draft is null) return;

        try
        {
            await _profiles.CreateAsync(draft);
            _log.LogInformation("Profile '{Name}' created via UI", draft.Name);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create profile '{Name}'", draft.Name);
            await _dialogs.ConfirmAsync(
                "Could not create profile",
                $"{ex.Message}\n\nCheck that the name is unique.",
                "OK");
        }
    }

    [RelayCommand]
    private async Task BulkAddAsync()
    {
        var didCreate = await _dialogs.ShowBulkCreateProfilesAsync();
        if (didCreate) await ReloadAsync();
    }

    /// <summary>
    /// Inline "↻ FP" button on each profile card — calls
    /// IFingerprintService.RegenerateAsync without leaving the page.
    /// Shows a brief toast/dialog with the new score.
    /// </summary>
    [RelayCommand]
    private async Task QuickRegenerateFpAsync(ProfileRowVm? selected)
    {
        if (selected is null) return;
        try
        {
            var score = await _fp.RegenerateAsync(selected.Profile.Name);
            await _dialogs.ConfirmAsync(
                "Fingerprint regenerated",
                $"'{selected.Profile.Name}' → score {score.Overall}/100 ({score.Label}).\n" +
                $"Identity {score.Identity} · Hardware {score.Hardware} · " +
                $"Network {score.Network} · Automation {score.Automation}.\n\n" +
                "The fresh payload will be picked up on the NEXT browser launch.",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Quick FP regenerate failed for '{Name}'", selected.Profile.Name);
            await _dialogs.ConfirmAsync(
                "Could not regenerate fingerprint",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task EditAsync(ProfileRowVm? selected)
    {
        if (selected is null) return;

        var edited = await _dialogs.ShowProfileEditorAsync(selected.Profile);
        if (edited is null) return;

        try
        {
            await _profiles.UpdateAsync(edited);
            _log.LogInformation("Profile '{Name}' updated via UI", edited.Name);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to update profile '{Name}'", edited.Name);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileRowVm? selected)
    {
        if (selected is null) return;
        var p = selected.Profile;

        if (selected.IsRunning)
        {
            await _dialogs.ConfirmAsync(
                "Profile is running",
                $"Stop '{p.Name}' before deleting it.",
                "OK");
            return;
        }

        var ok = await _dialogs.ConfirmAsync(
            $"Delete profile '{p.Name}'?",
            "This permanently removes the profile from the database. " +
            "Run history is kept. The user-data-dir on disk is NOT touched " +
            "by this — clean it up manually if you want a fresh start.",
            "Delete");
        if (!ok) return;

        try
        {
            await _profiles.DeleteAsync(p.Name);
            _log.LogInformation("Profile '{Name}' deleted via UI", p.Name);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete profile '{Name}'", p.Name);
        }
    }

    [RelayCommand]
    private async Task StartAsync(ProfileRowVm? selected)
    {
        if (selected is null) return;
        if (selected.IsRunning) return;
        if (selected.IsStarting) return; // double-click guard

        // Show the spinner immediately. The browser launch (chromedriver
        // spawn + chrome.exe boot) can take 3-10s, and the user pressed
        // a button — they want feedback now, not after we win the race
        // with chromedriver. The spinner clears when ActiveChanged fires
        // (success) or when an exception bubbles up (failure).
        selected.IsStarting = true;
        try
        {
            // Force the launch onto the thread pool. RealProfileRunner
            // → BrowserLauncher does heavy synchronous work (Selenium's
            // ChromeDriver ctor, file I/O for chromedriver log path, etc)
            // and any of those can pin the UI thread for several seconds
            // even though the surface is "async". Task.Run guarantees the
            // sync continuation runs off-thread.
            var runId = await Task.Run(() => _runner.StartAsync(selected.Profile));
            _log.LogInformation("Profile '{Name}' start requested → run #{Run}",
                selected.Profile.Name, runId);
            // ActiveChanged event will refresh IsRunning; no manual sync needed.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to start profile '{Name}'", selected.Profile.Name);
            await _dialogs.ConfirmAsync(
                "Could not start profile",
                ex.Message,
                "OK",
                ConfirmSeverity.Error);
        }
        finally
        {
            // Ensure the flag clears on either success path. If the
            // launch succeeded, IsRunning is now true via ActiveChanged
            // and IsStarting going back to false is invisible. If it
            // failed, the row goes back to its idle state (Start
            // button reappears). Either way: flag never sticks.
            selected.IsStarting = false;
        }
    }

    [RelayCommand]
    private async Task StopAsync(ProfileRowVm? selected)
    {
        if (selected is null) return;
        if (!selected.IsRunning) return;

        try
        {
            // driver.Quit() is synchronous and blocks until Chromium
            // tears down — push it off the UI thread for the same
            // reason as StartAsync.
            var stopped = await Task.Run(() => _runner.StopAsync(selected.Profile.Name));
            _log.LogInformation("Profile '{Name}' stop requested (was-running={Was})",
                selected.Profile.Name, stopped);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to stop profile '{Name}'", selected.Profile.Name);
        }
    }

    /// <summary>
    /// Bulk-start every checked profile that's currently idle.
    /// Skips already-running rows; each launch is awaited
    /// sequentially so the user sees rows turning green one by
    /// one rather than a chromedriver-storm parallel race that
    /// can saturate the box.
    /// </summary>
    [RelayCommand]
    private async Task BulkStartAsync()
    {
        var picks = Items.Where(r => r.IsSelected && !r.IsRunning && !r.IsStarting).ToList();
        if (picks.Count == 0) return;

        // Phase 64 — staggered launches via the run queue. Replace the
        // previous "loop and start synchronously" path which serialised
        // launches and gave the user no control over pacing. Now: ask
        // for stagger seconds + concurrency cap; enqueue all picks at
        // computed times; the RunQueueService dispatcher fires them.
        var opts = await _dialogs.ShowBulkStartOptionsAsync(picks.Count);
        if (opts is null) return;

        var names = picks.Select(r => r.Profile.Name).ToList();
        if (_queue is not null)
        {
            var ids = _queue.EnqueueBatch(names, opts.StaggerSeconds, opts.MaxConcurrent, "bulk");
            _log.LogInformation(
                "Bulk-start: enqueued {N} profile(s) stagger={S}s cap={C} — see Queue page",
                ids.Count, opts.StaggerSeconds, opts.MaxConcurrent);
            // Phase 65 — DO NOT set row.IsStarting=true here. The queue
            // dispatcher will eventually call _runner.StartAsync() which
            // populates ActiveProfileNames; the existing ActiveChanged
            // event subscription drives row.IsRunning automatically. If
            // we set IsStarting=true here without ever resetting it, the
            // bulk filter `!r.IsStarting` would permanently exclude the
            // row from future bulk operations — see audit finding #1/#10.
            // The queue page is the source of truth for "what's pending".
        }
        else
        {
            // Fallback if queue not available (shouldn't happen with DI).
            foreach (var row in picks)
            {
                row.IsStarting = true;
                try
                {
                    var runId = await Task.Run(() => _runner.StartAsync(row.Profile));
                    _log.LogInformation("Bulk-start (legacy path): '{Name}' → run #{Run}",
                        row.Profile.Name, runId);
                    if (opts.StaggerSeconds > 0)
                        await Task.Delay(TimeSpan.FromSeconds(opts.StaggerSeconds));
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Bulk-start failed for '{Name}'", row.Profile.Name);
                }
                finally { row.IsStarting = false; }
            }
        }
    }

    /// <summary>
    /// Phase 64 — Bulk Test Proxies. Iterates selected rows that have
    /// a proxy assigned and runs the proxy tester sequentially. Updates
    /// the proxy's health/latency in DB so the next launch sees fresh
    /// state.
    /// </summary>
    [RelayCommand]
    private async Task BulkTestProxiesAsync()
    {
        var picks = Items.Where(r => r.IsSelected && !string.IsNullOrEmpty(r.ProxySlug)).ToList();
        if (picks.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "No proxies to test",
                "Selected profiles have no proxy assigned.",
                "OK");
            return;
        }
        if (_proxyTester is null || _proxyService is null)
        {
            await _dialogs.ConfirmAsync(
                "Proxy services unavailable",
                "Internal: IProxyTester / IProxyService not wired in.",
                "OK", ConfirmSeverity.Error);
            return;
        }

        _log.LogInformation("Bulk-test: probing {N} proxies", picks.Count);
        var ok = 0; var fail = 0;
        foreach (var row in picks)
        {
            try
            {
                var proxy = await _proxyService.GetAsync(row.ProxySlug!);
                if (proxy is null) { fail++; continue; }
                var result = await _proxyTester.TestAsync(proxy);
                await _proxyService.RecordTestResultAsync(proxy.Slug, result);
                if (result.Ok) ok++; else fail++;
            }
            catch (Exception ex)
            {
                fail++;
                _log.LogWarning(ex, "Bulk-test failed for proxy on '{P}'", row.Profile.Name);
            }
        }
        await _dialogs.ConfirmAsync(
            "Proxy test complete",
            $"{ok} ok · {fail} failed (out of {picks.Count}). " +
            "Open the Proxies page for full diagnostics.",
            "OK");
    }

    /// <summary>
    /// Phase 64 — Bulk Self-Check. Triggers a fresh self-check probe on
    /// each selected profile (whether or not it's running). Used to
    /// re-score the whole farm after a major fingerprint template
    /// change or Chromium update.
    /// </summary>
    [RelayCommand]
    private async Task BulkSelfCheckAsync()
    {
        var picks = Items.Where(r => r.IsSelected).ToList();
        if (picks.Count == 0) return;
        var ok = await _dialogs.ConfirmAsync(
            $"Self-check {picks.Count} profile(s)?",
            "Each profile briefly launches its browser, runs ~25 fingerprint probes, and tears down. " +
            "Stagger is the same as Bulk Start — choose carefully on big batches.",
            "Run");
        if (!ok) return;
        var opts = await _dialogs.ShowBulkStartOptionsAsync(picks.Count);
        if (opts is null) return;

        // Phase 66 — probe-only launches: runAssignedScript=false (skip
        // the user's GoodMedika / SERP-engagement script), restoreSession=false
        // (skip the 30-60s cookie/storage restore since self-check is
        // state-independent — canvas/audio/WebGL probes don't need real
        // cookies). Self-check itself is auto-scheduled by Bootstrap 3s
        // after the browser settles. Each profile is up for ~5s instead
        // of ~minutes, so 100 profiles can be re-scored in a few minutes.
        var names = picks.Select(r => r.Profile.Name).ToList();
        if (_queue is not null)
        {
            _queue.EnqueueBatch(names, opts.StaggerSeconds, opts.MaxConcurrent,
                source: "self-check", probeOnly: true);
            _log.LogInformation(
                "Bulk self-check: enqueued {N} probe-only launch(es)", names.Count);
        }
    }

    /// <summary>
    /// Bulk-delete every checked profile that isn't running.
    /// One confirm dialog up front — no per-row prompts — but
    /// running rows are reported back as "skipped" so the user
    /// understands why they're still in the list.
    /// </summary>
    [RelayCommand]
    private async Task BulkDeleteAsync()
    {
        var picks    = Items.Where(r => r.IsSelected).ToList();
        if (picks.Count == 0) return;

        var running  = picks.Where(r => r.IsRunning).Select(r => r.Profile.Name).ToList();
        var deletable = picks.Where(r => !r.IsRunning).ToList();

        if (deletable.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "Nothing to delete",
                $"All {picks.Count} selected profile(s) are still running. " +
                "Stop them first, then bulk-delete.",
                "OK");
            return;
        }

        var msg = $"Permanently delete {deletable.Count} profile(s)?";
        if (running.Count > 0)
            msg += $"\n\n{running.Count} running profile(s) will be skipped: " +
                   string.Join(", ", running.Take(5)) +
                   (running.Count > 5 ? $", +{running.Count - 5} more" : "");

        var ok = await _dialogs.ConfirmAsync(
            msg,
            "Run history is kept. The user-data-dir on disk is NOT " +
            "touched — clean it up manually if you want a fresh start.",
            "Delete",
            ConfirmSeverity.Danger);
        if (!ok) return;

        var failed = 0;
        foreach (var row in deletable)
        {
            try
            {
                await _profiles.DeleteAsync(row.Profile.Name);
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogError(ex, "Bulk-delete failed for '{Name}'", row.Profile.Name);
            }
        }

        _log.LogInformation("Bulk-delete: removed {Removed}, failed {Failed}",
            deletable.Count - failed, failed);
        await ReloadAsync();
    }
}

/// <summary>
/// Toggle for the Profiles list rendering. Cards mirror the legacy
/// web's tile layout (icon + name + status pill + 2-up stats);
/// Table is the existing DataGrid view.
/// </summary>
public enum ProfilesViewMode
{
    Cards,
    Table,
}

/// <summary>
/// Row VM that wraps a <see cref="Profile"/> with a mutable
/// IsRunning flag. The page-level VM updates the flag whenever
/// IProfileRunner reports an active-set change; the DataGrid
/// row's Start/Stop buttons toggle visibility based on this.
///
/// IsStarting flips on while the launch is in flight (between
/// StartCommand fire and either success or failure). The row's
/// action cell shows a spinner during that window so the user
/// gets immediate feedback even when chromedriver takes a few
/// seconds to spawn the browser.
///
/// IsSelected is the bulk-actions checkbox state. The page VM
/// listens to PropertyChanged for the SelectedCount aggregate
/// that drives the bulk-action button labels.
/// </summary>
public sealed partial class ProfileRowVm : ObservableObject
{
    public Profile Profile { get; }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private bool _isSelected;

    public ProfileRowVm(Profile profile, bool isRunning)
    {
        Profile = profile;
        _isRunning = isRunning;
    }

    // ─── Status flags for color triggers ────────────────────────
    // These are derived booleans the View uses to switch a status
    // pill's color between green (running) / blue (starting) /
    // teal (ready) / amber (not-ready) / gray (idle baseline).
    public bool IsReady    => !IsRunning && !IsStarting && Profile.IsReady;
    public bool IsNotReady => !IsRunning && !IsStarting && !Profile.IsReady;

    /// <summary>
    /// Per-card accent hue. Derived deterministically from a hash of
    /// the profile name so the same profile always gets the same
    /// stripe color across reloads. Maps to one of the eight Hue*
    /// brushes already used elsewhere (Profiles green, Sessions violet,
    /// etc.). Form-factor still wins where it's known: mobile profiles
    /// always get teal so the visual scan ranks form-factor first.
    /// </summary>
    public string AccentBrushKey
    {
        get
        {
            // Mobile FP templates contain "phone" / "mobile" / "android" / "iphone"
            var t = Profile.TemplateId?.ToLowerInvariant() ?? "";
            if (t.Contains("phone") || t.Contains("mobile")
                || t.Contains("android") || t.Contains("iphone"))
                return "HueTeal";
            if (t.Contains("mac") || t.Contains("apple"))
                return "HueSlate";
            if (t.Contains("gaming") || t.Contains("rtx") || t.Contains("nvidia"))
                return "HueGreen";
            if (t.Contains("amd") || t.Contains("radeon"))
                return "HueOrange";
            // Stable hash → 1 of 5 default hues for everything else.
            var h = (uint)(Profile.Name?.GetHashCode() ?? 0);
            return (h % 5) switch
            {
                0 => "HueBlue",
                1 => "HueIndigo",
                2 => "HueViolet",
                3 => "HuePink",
                _ => "HueAmber",
            };
        }
    }

    // Convenience exposed for direct binding from DataGrid columns.
    public string  Name        => Profile.Name;
    public string? GroupName   => Profile.GroupName;
    public string? TemplateId  => Profile.TemplateId;
    public string? ProxySlug   => Profile.ProxySlug;
    public int     RunCount    => Profile.RunCount;
    public DateTime? LastRunAt => Profile.LastRunAt;

    // ─── Card-only display helpers ──────────────────────────────
    /// <summary>Status label shown on cards: "running" / "ready" /
    /// "starting" / "idle". Phase 71w — replaced the em-dash idle
    /// state with the literal word "idle" so the pill reads as a
    /// proper status chip ("idle"/"ready"/"running") instead of
    /// rendering a single dash that looked like a stretched
    /// icon glitch.</summary>
    public string StatusText
        => IsStarting ? "starting"
         : IsRunning  ? "running"
         : Profile.IsReady ? "ready"
         : "idle";

    /// <summary>
    /// Compact "last run" age for card footers — "12m ago",
    /// "2d ago", or "never" for unused profiles.
    /// </summary>
    public string LastRunHuman
    {
        get
        {
            if (Profile.LastRunAt is null) return "never";
            var span = DateTime.UtcNow - Profile.LastRunAt.Value;
            if (span.TotalMinutes < 1)  return "just now";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
            if (span.TotalHours   < 24) return $"{(int)span.TotalHours}h ago";
            if (span.TotalDays    < 7)  return $"{(int)span.TotalDays}d ago";
            return Profile.LastRunAt.Value.ToString("yyyy-MM-dd");
        }
    }

    // Re-fire StatusText (and the IsReady/IsNotReady computed pair)
    // when the dependency flags change so cards update without an
    // explicit converter on each binding.
    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsNotReady));
    }
    partial void OnIsStartingChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsNotReady));
    }
}

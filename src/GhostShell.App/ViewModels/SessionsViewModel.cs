// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// "Session &amp; cookies" page — the per-profile identity hub.
/// Layout mirrors the legacy web's redesign:
///
///   [ profile sidebar ]   ┌── stat row (4 cards) ──────────┐
///                         │  LAST WARMUP / WARMUP STATUS / │
///                         │  COOKIE SNAPSHOTS / POOL COOKIES │
///                         ├── tab strip ───────────────────┤
///                         │  Warmup robot │ Cookies │       │
///                         │  Snapshots │ Chrome import      │
///                         └────────────────────────────────┘
///
/// Tab summary:
///   • Warmup robot — preset cards, site count, "Run warmup now"
///     button, "Warmup history" table at the bottom.
///   • Cookies — current pool of cookies for the selected profile
///     (read from the latest snapshot, since live read needs the
///     browser to be running).
///   • Snapshots — full list of cookie_snapshots for the profile.
///     Restore (promote-to-top) and Delete actions per row.
///   • Chrome import — file picker for a real Chrome User Data dir.
///     Phase-7 stub at the moment; the panel renders but the
///     import action is disabled with a placeholder note.
/// </summary>
public sealed partial class SessionsViewModel : BaseViewModel
{
    private readonly ISessionService  _sessions;
    private readonly IProfileService  _profiles;
    private readonly IProfileRunner   _runner;
    private readonly IWarmupService   _warmup;
    private readonly IChromeImporter  _chrome;
    private readonly IDialogService   _dialogs;
    private readonly ICookiePackService _packs;
    private readonly ILogger<SessionsViewModel> _log;

    // Theme brushes resolved once (avoid re-resolving inside hot
    // property setters that fire per row update).
    private readonly Brush _okBrush;
    private readonly Brush _warnBrush;
    private readonly Brush _errBrush;
    private readonly Brush _dimBrush;

    public SessionsViewModel(
        ISessionService sessions,
        IProfileService profiles,
        IProfileRunner  runner,
        IWarmupService  warmup,
        IChromeImporter chrome,
        IDialogService  dialogs,
        ICookiePackService packs,
        ILogger<SessionsViewModel> log)
    {
        _sessions = sessions;
        _profiles = profiles;
        _runner   = runner;
        _warmup   = warmup;
        _chrome   = chrome;
        _dialogs  = dialogs;
        _packs    = packs;
        _log      = log;

        _okBrush   = (Brush)(Application.Current?.TryFindResource("OkBrush")    ?? Brushes.LimeGreen);
        _warnBrush = (Brush)(Application.Current?.TryFindResource("WarnBrush")  ?? Brushes.Orange);
        _errBrush  = (Brush)(Application.Current?.TryFindResource("ErrBrush")   ?? Brushes.IndianRed);
        _dimBrush  = (Brush)(Application.Current?.TryFindResource("TextDim")    ?? Brushes.Gray);

        // Initial brush for the WARMUP STATUS card. Without this the
        // "—" placeholder renders against a null Brush and WPF logs a
        // binding error on every navigation.
        _warmupStatusBrush = _dimBrush;

        // Build preset cards once — the catalog is static. Each card
        // is pinned to a specific Hue from the app palette so the grid
        // reads as a colour map (mirrors the legacy web's per-preset
        // accent stripes).
        Brush Hue(string key) =>
            (Brush)(Application.Current?.TryFindResource(key) ?? Brushes.Gray);
        foreach (var p in _warmup.Presets)
        {
            var (accent, icon) = p.Id switch
            {
                "general"     => (Hue("HueBlue"),    "🧭"),
                "commerce_ua" => (Hue("HuePink"),    "🛒"),
                "medical"     => (Hue("HueGreen"),   "🩺"),
                "tech"        => (Hue("HueIndigo"),  "💻"),
                "news"        => (Hue("HueAmber"),   "📰"),
                "mobile"      => (Hue("HueTeal"),    "📱"),
                _             => (Hue("HueSlate"),   "🌐"),
            };
            Presets.Add(new PresetCard
            {
                Id          = p.Id,
                Label       = p.Label,
                Description = p.Description,
                SiteCount   = p.SiteCount,
                AccentBrush = accent,
                Icon        = icon,
            });
        }
        SelectedPreset = Presets.FirstOrDefault();
        if (SelectedPreset is not null) SelectedPreset.IsSelected = true;

        // Refresh the page whenever a run / warmup state changes —
        // both surfaces produce snapshots that the UI shows.
        _runner.ActiveChanged += (_, _) => DispatchReload();
        _warmup.ActiveChanged += (_, _) => DispatchReload();
    }

    // ──────────────────────────────────────────────────────────────
    // Profile sidebar
    // ──────────────────────────────────────────────────────────────

    public ObservableCollection<ProfileRow> ProfileRows { get; } = new();

    [ObservableProperty]
    private ProfileRow? _selectedProfileRow;

    partial void OnSelectedProfileRowChanged(ProfileRow? value)
    {
        DispatchReload();
        OnPropertyChanged(nameof(CanRunChromeImport));
    }

    public string ProfilesHeader =>
        ProfileRows.Count == 1 ? "1 profile" : $"{ProfileRows.Count} profiles";

    // ──────────────────────────────────────────────────────────────
    // Multi-select (batch warmup target)
    //
    // The focused row (SelectedProfileRow) drives stats/history. The
    // checked set is independent — when ≥1 row is checked, the
    // Run-warmup button targets the union; otherwise it targets the
    // focused row only. Mirrors the Profiles page bulk-select pattern.
    // ──────────────────────────────────────────────────────────────

    /// <summary>How many rows the user has ticked for batch warmup.</summary>
    public int CheckedCount => ProfileRows.Count(r => r.IsChecked);

    /// <summary>True if any rows are checked — drives the bulk-action strip visibility.</summary>
    public bool HasChecked => CheckedCount > 0;

    /// <summary>"3 selected" / "1 selected" — bulk-action strip label.</summary>
    public string BulkSelectionLabel =>
        CheckedCount == 1 ? "1 profile selected" : $"{CheckedCount} profiles selected";

    // ──────────────────────────────────────────────────────────────
    // Header stat boxes (top row, computed for the selected profile)
    // ──────────────────────────────────────────────────────────────

    [ObservableProperty] private string _lastWarmupValue    = "—";
    [ObservableProperty] private string _lastWarmupSubtitle = "no warmup yet";

    [ObservableProperty] private string _warmupStatusValue  = "—";
    [ObservableProperty] private Brush  _warmupStatusBrush;

    [ObservableProperty] private int _cookieSnapshotsCount;
    [ObservableProperty] private int _poolCookiesCount;

    // ──────────────────────────────────────────────────────────────
    // Tabs
    // ──────────────────────────────────────────────────────────────

    /// <summary>0=Warmup, 1=Cookies, 2=Snapshots, 3=Chrome import.</summary>
    [ObservableProperty] private int _selectedTabIndex;

    // ──────────────────────────────────────────────────────────────
    // Warmup tab state
    // ──────────────────────────────────────────────────────────────

    public ObservableCollection<PresetCard> Presets { get; } = new();

    [ObservableProperty] private PresetCard? _selectedPreset;

    /// <summary>Number of sites the engine should visit. Default 7.</summary>
    [ObservableProperty] private int _siteCount = 7;

    public ObservableCollection<WarmupHistoryRow> WarmupHistory { get; } = new();

    /// <summary>True iff the Run-warmup button should be enabled.</summary>
    public bool CanRunWarmup =>
        SelectedProfileRow is { IsRunning: false, IsWarmupRunning: false };

    /// <summary>
    /// "Run warmup now" / "Cancel warmup" toggle text. Adapts to the
    /// batch context: if rows are checked, we display the count, and
    /// flip to "Cancel" when any of them are mid-warmup so the same
    /// button can also batch-cancel.
    /// </summary>
    public string RunButtonLabel
    {
        get
        {
            var targets = TargetRowsForButton();
            var anyRunning = targets.Any(r => r.IsWarmupRunning);
            if (targets.Count <= 1)
                return anyRunning ? "■  Cancel warmup" : "▶  Run warmup now";
            return anyRunning
                ? $"■  Cancel {targets.Count} warmups"
                : $"▶  Run warmup on {targets.Count} profiles";
        }
    }

    /// <summary>
    /// Resolve which rows the Run button should act on:
    ///   • checked set if non-empty;
    ///   • focused row otherwise (or empty if no profile is focused).
    /// </summary>
    private IReadOnlyList<ProfileRow> TargetRowsForButton()
    {
        var checkedRows = ProfileRows.Where(r => r.IsChecked).ToList();
        if (checkedRows.Count > 0) return checkedRows;
        if (SelectedProfileRow is not null) return new[] { SelectedProfileRow };
        return Array.Empty<ProfileRow>();
    }

    // ──────────────────────────────────────────────────────────────
    // Snapshots tab state (port of the original flat list)
    // ──────────────────────────────────────────────────────────────

    public ObservableCollection<SessionSnapshot> Snapshots { get; } = new();

    [ObservableProperty] private bool _isSnapshotsEmpty = true;

    // ──────────────────────────────────────────────────────────────
    // Cookies tab — per-domain inspector
    //
    // The grid binds to <see cref="CookieRows"/>, which is filtered
    // through <see cref="CookieFilter"/>. Source of truth is the
    // latest snapshot's payload — we lazy-load it on tab activation
    // (or whenever the focused profile changes).
    // ──────────────────────────────────────────────────────────────

    public ObservableCollection<CookieRowView> CookieRows { get; } = new();
    private List<CookieRowView> _allCookieRows = new();

    [ObservableProperty] private string? _cookieFilter;
    [ObservableProperty] private bool _isCookiesEmpty = true;
    [ObservableProperty] private string _cookiesSourceLabel = "—";

    partial void OnCookieFilterChanged(string? value) => ApplyCookieFilter();

    // ──────────────────────────────────────────────────────────────
    // Chrome-import tab state
    // ──────────────────────────────────────────────────────────────

    public ObservableCollection<ChromeProfileSource> ChromeSources { get; } = new();

    [ObservableProperty] private ChromeProfileSource? _selectedChromeSource;
    [ObservableProperty] private bool _chromeImportCookies = true;
    [ObservableProperty] private int _chromeHistoryDays = 90;
    [ObservableProperty] private int _chromeMaxUrls = 1000;
    [ObservableProperty] private bool _chromeSkipSensitive = true;
    [ObservableProperty] private bool _isChromeImporting;
    [ObservableProperty] private string? _chromeImportStatus;

    /// <summary>True when the Run-import button should be enabled.</summary>
    public bool CanRunChromeImport
        => !IsChromeImporting
        && SelectedChromeSource is not null
        && SelectedProfileRow is not null;

    partial void OnSelectedChromeSourceChanged(ChromeProfileSource? value)
        => OnPropertyChanged(nameof(CanRunChromeImport));
    partial void OnIsChromeImportingChanged(bool value)
        => OnPropertyChanged(nameof(CanRunChromeImport));

    // ──────────────────────────────────────────────────────────────
    // Lifecycle
    // ──────────────────────────────────────────────────────────────

    public override async Task OnNavigatedToAsync()
    {
        await ReloadProfilesAsync();
        await ReloadSelectedAsync();
        // Chrome import discovery is local I/O only — no network — so
        // running it on first nav is fine. The dropdown stays empty
        // until the call returns; the user can also force-refresh via
        // the Detect button.
        if (ChromeSources.Count == 0)
            _ = DiscoverChromeSourcesAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        await ReloadProfilesAsync();
        await ReloadSelectedAsync();
    }

    private void DispatchReload()
    {
        if (Application.Current?.Dispatcher is { } d)
            d.BeginInvoke(() => _ = ReloadSelectedAsync());
    }

    private async Task ReloadProfilesAsync()
    {
        IsBusy = true;
        try
        {
            var keepName = SelectedProfileRow?.Name;

            var fromDb = await _profiles.ListAsync();
            // Some snapshots may belong to deleted profiles; show those as
            // tombstone rows so the user can clean them up. Same approach
            // as the previous SessionsViewModel.
            var snapshotProfiles = (await _sessions.ListAsync(limit: 1000))
                .Select(s => s.ProfileName);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in fromDb) names.Add(p.Name);
            foreach (var n in snapshotProfiles) names.Add(n);

            // Detach handlers from the soon-to-be-discarded rows so
            // they're collectible. Without this, a long-running app
            // could accumulate one closure per Reload click.
            foreach (var old in ProfileRows)
                old.PropertyChanged -= OnProfileRowPropertyChanged;

            // Preserve checked-state across reloads — the user's
            // selection shouldn't get wiped when ActiveChanged fires
            // mid-batch, only when they explicitly click Clear.
            var preserveChecked = new HashSet<string>(
                ProfileRows.Where(r => r.IsChecked).Select(r => r.Name),
                StringComparer.OrdinalIgnoreCase);

            ProfileRows.Clear();
            foreach (var n in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var row = new ProfileRow
                {
                    Name            = n,
                    IsRunning       = _runner.ActiveProfileNames.Contains(n),
                    IsWarmupRunning = _warmup.ActiveProfileNames.Contains(n),
                    IsChecked       = preserveChecked.Contains(n),
                };
                // Re-broadcast the bulk-affecting flags whenever a row
                // changes. Doing it via PropertyChanged (not the
                // partial Changed callbacks) keeps ProfileRow ignorant
                // of its parent VM — the row stays a self-contained
                // model and the wiring lives on this side.
                row.PropertyChanged += OnProfileRowPropertyChanged;
                ProfileRows.Add(row);
            }

            // Bulk-state may have changed if any preserved-checked
            // profiles disappeared; re-broadcast.
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(HasChecked));
            OnPropertyChanged(nameof(BulkSelectionLabel));
            OnPropertyChanged(nameof(RunButtonLabel));

            // Restore selection — by name if possible, else first row.
            SelectedProfileRow =
                ProfileRows.FirstOrDefault(r => string.Equals(r.Name, keepName, StringComparison.OrdinalIgnoreCase))
                ?? ProfileRows.FirstOrDefault();

            OnPropertyChanged(nameof(ProfilesHeader));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sessions: profile list reload failed");
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Reloads everything that depends on the selected profile —
    /// stats, warmup history, snapshots list. Cheap (≤ a few SQL
    /// queries); we call it on every selection change and on every
    /// runner/warmup state change.
    /// </summary>
    private async Task ReloadSelectedAsync()
    {
        // Update per-row liveness flags first so the sidebar re-paints
        // even if no row is currently selected.
        foreach (var row in ProfileRows)
        {
            row.IsRunning       = _runner.ActiveProfileNames.Contains(row.Name);
            row.IsWarmupRunning = _warmup.ActiveProfileNames.Contains(row.Name);
        }
        OnPropertyChanged(nameof(CanRunWarmup));
        OnPropertyChanged(nameof(RunButtonLabel));

        var profile = SelectedProfileRow?.Name;
        if (profile is null)
        {
            ResetStats();
            WarmupHistory.Clear();
            Snapshots.Clear();
            IsSnapshotsEmpty = true;
            return;
        }

        try
        {
            // Stats — last warmup + warmup status from warmup_runs.
            var last = await _warmup.GetLatestAsync(profile);
            if (last is null)
            {
                LastWarmupValue    = "never";
                LastWarmupSubtitle = "no warmup yet";
                WarmupStatusValue  = "—";
                WarmupStatusBrush  = _dimBrush;
            }
            else
            {
                LastWarmupValue    = HumanizeAgo(last.StartedAt);
                LastWarmupSubtitle = $"{last.Preset ?? "?"} · {last.SitesSucceeded}/{last.SitesPlanned} ok";
                WarmupStatusValue  = last.Status switch
                {
                    "running" => "running…",
                    "ok"      => "ok",
                    "partial" => "partial",
                    "failed"  => "failed",
                    _         => last.Status,
                };
                WarmupStatusBrush = last.Status switch
                {
                    "ok"      => _okBrush,
                    "partial" => _warnBrush,
                    "failed"  => _errBrush,
                    _         => _dimBrush,
                };
            }

            // Stats — snapshot count + total cookies across snapshots.
            var snapshots = await _sessions.ListAsync(profile);
            CookieSnapshotsCount = snapshots.Count;
            PoolCookiesCount     = snapshots.Sum(s => s.CookieCount);

            // Warmup history table
            var history = await _warmup.ListHistoryAsync(profile, limit: 50);
            WarmupHistory.Clear();
            foreach (var r in history)
                WarmupHistory.Add(WarmupHistoryRow.From(r, _okBrush, _warnBrush, _errBrush, _dimBrush));

            // Snapshots table
            Snapshots.Clear();
            foreach (var s in snapshots) Snapshots.Add(s);
            IsSnapshotsEmpty = Snapshots.Count == 0;

            // Cookies tab — load the latest snapshot's payload. We do
            // it here even if the user is on a different tab because
            // it's cheap (≤ 100KB JSON) and avoids a tab-switch lag.
            await ReloadCookiesAsync(snapshots.FirstOrDefault());
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sessions: per-profile reload failed for '{Profile}'", profile);
        }
    }

    private void ResetStats()
    {
        LastWarmupValue       = "—";
        LastWarmupSubtitle    = "select a profile";
        WarmupStatusValue     = "—";
        WarmupStatusBrush     = _dimBrush;
        CookieSnapshotsCount  = 0;
        PoolCookiesCount      = 0;
    }

    // ──────────────────────────────────────────────────────────────
    // Warmup commands
    // ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectPreset(PresetCard? card)
    {
        if (card is null) return;
        foreach (var p in Presets) p.IsSelected = false;
        card.IsSelected = true;
        SelectedPreset = card;
        // Reset siteCount to a sensible default for the new preset.
        SiteCount = Math.Min(SiteCount, card.SiteCount);
        if (SiteCount < 1) SiteCount = Math.Min(7, card.SiteCount);
    }

    [RelayCommand]
    private async Task RunWarmupAsync()
    {
        // Guard 1: must have a preset. With the View defaulting to
        // "General" on construction this only fires if Presets is
        // empty (catalog mis-configured). Surface a real message
        // rather than silently dropping the click.
        var preset = SelectedPreset;
        if (preset is null)
        {
            await _dialogs.ConfirmAsync(
                "No preset chosen",
                "Pick a preset card above (General / Commerce UA / Medical / etc.) before running a warmup.",
                "OK", ConfirmSeverity.Warning);
            return;
        }

        // Guard 2: must have at least one target. Either the user
        // checked some rows or has a focused profile. If neither, tell
        // them what to do — silent return is the worst possible UX.
        var targets = TargetRowsForButton();
        if (targets.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "No profile selected",
                ProfileRows.Count == 0
                    ? "Create a profile first (Profiles page → New) — there's nothing to warm up."
                    : "Click a profile in the sidebar to focus it, or tick the checkbox on one or more profiles to run warmup on a batch.",
                "OK", ConfirmSeverity.Warning);
            return;
        }

        // Cancel branch — if ANY target has an active warmup, the
        // button is in "cancel" mode and we cancel every running one.
        if (targets.Any(r => r.IsWarmupRunning))
        {
            foreach (var row in targets.Where(r => r.IsWarmupRunning))
            {
                try { await _warmup.CancelAsync(row.Name); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Cancel warmup failed for '{Profile}'", row.Name);
                }
            }
            return;
        }

        // Launch branch — split into "ready" vs "blocked" so the user
        // sees a single dialog summarising what we couldn't start.
        var sitesToVisit = Math.Clamp(SiteCount, 1, preset.SiteCount);
        var blocked = targets.Where(r => r.IsRunning).Select(r => r.Name).ToList();
        var ready   = targets.Where(r => !r.IsRunning).ToList();

        if (ready.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "Profiles are all currently running",
                "Stop the active monitor runs first — warmup needs an exclusive Chromium session.",
                "OK", ConfirmSeverity.Warning);
            return;
        }

        // Stagger launches by ~1.5s. Chromium startup is heavy and
        // hammering N launches in the same tick can exhaust resources
        // (chrome.exe spawn time alone is ~600ms). The stagger also
        // distributes proxy DNS lookups so a shared proxy doesn't
        // spike on simultaneous connect attempts.
        var started = 0;
        var failed  = new List<(string Name, string Reason)>();
        foreach (var row in ready)
        {
            try
            {
                await _warmup.StartAsync(row.Name, preset.Id, sitesToVisit, "manual");
                started++;
                _log.LogInformation(
                    "Warmup started: profile='{Profile}' preset={Preset} sites={N}",
                    row.Name, preset.Id, sitesToVisit);
            }
            catch (InvalidOperationException ex)
            {
                failed.Add((row.Name, ex.Message));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Warmup start failed for '{Profile}'", row.Name);
                failed.Add((row.Name, ex.Message));
            }

            // Don't sleep after the last one.
            if (row != ready[^1])
                await Task.Delay(TimeSpan.FromMilliseconds(1500));
        }

        // Surface a single summary if anything went sideways.
        if (blocked.Count == 0 && failed.Count == 0)
            return; // happy path — UI auto-refreshes via ActiveChanged

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Started: {started}");
        if (blocked.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Skipped (already running monitor): {blocked.Count}");
            foreach (var n in blocked) sb.AppendLine($"  • {n}");
        }
        if (failed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Could not start: {failed.Count}");
            foreach (var (n, why) in failed) sb.AppendLine($"  • {n} — {why}");
        }
        await _dialogs.ConfirmAsync(
            "Warmup batch result",
            sb.ToString(),
            "OK",
            failed.Count > 0 ? ConfirmSeverity.Warning : ConfirmSeverity.Info);
    }

    /// <summary>Untick every row. Bound to the bulk-strip × button.</summary>
    [RelayCommand]
    private void ClearChecked()
    {
        foreach (var r in ProfileRows) r.IsChecked = false;
    }

    /// <summary>
    /// Tick every visible row. Bound to the "all" link in the bulk strip.
    /// </summary>
    [RelayCommand]
    private void CheckAll()
    {
        foreach (var r in ProfileRows) r.IsChecked = true;
    }

    /// <summary>
    /// Per-row PropertyChanged handler. Re-broadcasts the bulk-derived
    /// computed properties so the bulk strip + Run-button label
    /// re-render when any row's IsChecked / IsWarmupRunning flips.
    /// </summary>
    private void OnProfileRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileRow.IsChecked))
        {
            OnPropertyChanged(nameof(CheckedCount));
            OnPropertyChanged(nameof(HasChecked));
            OnPropertyChanged(nameof(BulkSelectionLabel));
            OnPropertyChanged(nameof(RunButtonLabel));
        }
        else if (e.PropertyName is nameof(ProfileRow.IsWarmupRunning) or nameof(ProfileRow.IsRunning))
        {
            // Run-button label flips between "Run / Cancel" based on
            // any-running-in-target-set; needs a refresh too.
            OnPropertyChanged(nameof(RunButtonLabel));
            OnPropertyChanged(nameof(CanRunWarmup));
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Snapshots commands (port of original flow)
    // ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DeleteSnapshotAsync(SessionSnapshot? selected)
    {
        if (selected is null) return;
        var ok = await _dialogs.ConfirmAsync(
            $"Delete snapshot #{selected.Id}?",
            $"Captured {selected.CookieCount} cookie(s) for '{selected.ProfileName}' on " +
            $"{selected.CreatedAt:yyyy-MM-dd HH:mm}. This is permanent.",
            "Delete", ConfirmSeverity.Danger);
        if (!ok) return;

        try
        {
            await _sessions.DeleteAsync(selected.Id);
            await ReloadSelectedAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Delete snapshot #{Id} failed", selected.Id);
            await _dialogs.ConfirmAsync("Could not delete snapshot",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RestoreSnapshotAsync(SessionSnapshot? selected)
    {
        if (selected is null) return;
        if (_runner.ActiveProfileNames.Contains(selected.ProfileName))
        {
            await _dialogs.ConfirmAsync(
                "Profile is currently running",
                $"Stop '{selected.ProfileName}' first. The next launch will " +
                "auto-restore the latest snapshot, which includes this one " +
                "if it's the most recent.",
                "OK", ConfirmSeverity.Warning);
            return;
        }
        var latest = await _sessions.GetLatestAsync(selected.ProfileName);
        if (latest is not null && latest.Id == selected.Id)
        {
            await _dialogs.ConfirmAsync(
                "Already the latest",
                $"This is the most recent snapshot for '{selected.ProfileName}'. " +
                "The next time you launch the profile it'll be auto-restored.",
                "OK", ConfirmSeverity.Info);
            return;
        }
        try
        {
            var payload = await _sessions.GetPayloadAsync(selected.Id);
            if (payload is null)
            {
                await _dialogs.ConfirmAsync("Snapshot missing",
                    "This snapshot has no payload (corrupted or deleted).",
                    "OK", ConfirmSeverity.Error);
                return;
            }
            await _sessions.SaveAsync(
                profileName: selected.ProfileName,
                payload:     payload,
                runId:       null,
                trigger:     "manual",
                reason:      $"Restore promoted from snapshot #{selected.Id}");
            await ReloadSelectedAsync();
            await _dialogs.ConfirmAsync(
                "Promoted",
                "Saved as a new manual snapshot at the top of the list. " +
                "Next launch of the profile will pick it up.",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Restore-promote failed for snapshot #{Id}", selected.Id);
            await _dialogs.ConfirmAsync("Could not restore",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task SaveSnapshotAsPackAsync(SessionSnapshot? selected)
    {
        if (selected is null) return;
        try
        {
            // Load the full snapshot payload: cookies + storage.
            // Reuse ISessionService.GetPayloadAsync which is already
            // wired for decompression + deserialization.
            var payload = await _sessions.GetPayloadAsync(selected.Id);
            if (payload is null)
            {
                await _dialogs.ConfirmAsync("Snapshot missing",
                    "This snapshot has no payload (corrupted or deleted).",
                    "OK", ConfirmSeverity.Error);
                return;
            }

            // Generate a default pack name based on the profile name +
            // snapshot ID. User can rename the pack later in Cookie Pool.
            var packName = $"{selected.ProfileName} — snapshot #{selected.Id}";

            // Build the pack metadata row. Slug must be unique; we use
            // a timestamp-based format so parallel saves don't collide.
            // Domains are derived from the snapshot's cookies.
            var domains = payload.Cookies
                .Select(c => c.Domain.TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d)
                .ToList();

            var packMeta = new CookiePack
            {
                Label         = packName,
                Slug          = $"snapshot-{selected.ProfileName}-{selected.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                Domains       = domains,
                CookiesCount  = payload.Cookies.Count,
                AgeDays       = 0,
                CaptchaRate   = 0,
            };

            // Persist to the cookie_packs table via ICookiePackService.UpsertAsync.
            var packId = await _packs.UpsertAsync(packMeta, payload);

            _log.LogInformation(
                "Snapshot #{SnapshotId} saved as pack #{PackId}: '{Label}'",
                selected.Id, packId, packMeta.Label);

            await _dialogs.ConfirmAsync(
                $"Saved as cookie pack '{packMeta.Label}'",
                $"Pack #{packId}\n" +
                $"Cookies: {payload.Cookies.Count}\n" +
                $"Domains: {domains.Count}\n\n" +
                "Open Cookie Pool from the sidebar to manage or rename your saved packs.",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save snapshot as pack failed for snapshot #{Id}", selected.Id);
            await _dialogs.ConfirmAsync("Could not save as pack",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Cookies tab
    // ──────────────────────────────────────────────────────────────

    private async Task ReloadCookiesAsync(SessionSnapshot? latest)
    {
        _allCookieRows = new List<CookieRowView>();
        if (latest is null)
        {
            CookiesSourceLabel = "no snapshot yet";
            ApplyCookieFilter();
            return;
        }
        try
        {
            var payload = await _sessions.GetPayloadAsync(latest.Id);
            if (payload is null)
            {
                CookiesSourceLabel = $"snapshot #{latest.Id} (payload missing)";
                ApplyCookieFilter();
                return;
            }
            CookiesSourceLabel = $"snapshot #{latest.Id} · {latest.CreatedAt:yyyy-MM-dd HH:mm} · " +
                                 $"{payload.Cookies.Count} cookies";
            _allCookieRows = payload.Cookies
                .OrderBy(c => c.Domain.TrimStart('.'), StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CookieRowView.From)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cookies tab load failed for snapshot #{Id}", latest.Id);
            CookiesSourceLabel = "load failed: " + ex.Message;
        }
        ApplyCookieFilter();
    }

    private void ApplyCookieFilter()
    {
        IEnumerable<CookieRowView> q = _allCookieRows;
        if (!string.IsNullOrWhiteSpace(CookieFilter))
        {
            var needle = CookieFilter.Trim();
            q = q.Where(r =>
                r.Domain.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || r.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        CookieRows.Clear();
        foreach (var r in q) CookieRows.Add(r);
        IsCookiesEmpty = CookieRows.Count == 0;
    }

    [RelayCommand]
    private async Task ExportCookiesJsonAsync()
    {
        if (_allCookieRows.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export cookies as JSON",
            FileName = $"{SelectedProfileRow?.Name ?? "profile"}-cookies.json",
            Filter = "JSON (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = ".json",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var raw = _allCookieRows.Select(r => new
            {
                name = r.Name,
                domain = r.Domain,
                path = r.Path,
                value = r.Value,
                expiry = r.ExpiresUnixSec,
                secure = r.Secure,
                httpOnly = r.HttpOnly,
                sameSite = r.SameSite,
            }).ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(raw,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(dlg.FileName, json);
            await _dialogs.ConfirmAsync(
                "Exported", $"Wrote {_allCookieRows.Count} cookie(s) to:\n{dlg.FileName}",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Cookies export failed");
            await _dialogs.ConfirmAsync(
                "Export failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task SaveFilteredCookiesAsPackAsync()
    {
        // Guard: must have cookies to save. If the visible filter is
        // empty, offer to save all cookies instead.
        if (_allCookieRows.Count == 0) return;
        if (CookieRows.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "No cookies match filter",
                "The current filter has no results. Clear the filter to save all cookies from the latest snapshot.",
                "OK", ConfirmSeverity.Info);
            return;
        }

        try
        {
            // Build the SessionPayload from filtered cookies + empty
            // storage (only snapshots capture storage; filtered cookies
            // are display-only). Use SessionPayloadJson to serialize
            // into the same format that cookie_packs expects.
            var filteredCookies = CookieRows.Select(r => new CookieEntry
            {
                Domain      = r.Domain,
                Name        = r.Name,
                Value       = r.Value,
                Path        = r.Path,
                ExpiresUnixSec = r.ExpiresUnixSec,
                Secure      = r.Secure,
                HttpOnly    = r.HttpOnly,
                SameSite    = r.SameSite,
            }).ToList();

            var payload = new SessionPayload
            {
                Cookies = filteredCookies,
                Storage = Array.Empty<StorageEntry>(),
            };

            // Generate a pack name that describes the filtered set.
            // Include count + optional filter term for context.
            var filterDesc = !string.IsNullOrWhiteSpace(CookieFilter)
                ? $" (filter: {CookieFilter.Trim()})"
                : string.Empty;
            var packName = $"{SelectedProfileRow?.Name ?? "profile"} — {filteredCookies.Count} cookies{filterDesc}";

            // Build the pack metadata row. Derive domains from the
            // filtered cookies to match the actual pack contents.
            var domains = filteredCookies
                .Select(c => c.Domain.TrimStart('.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d)
                .ToList();

            var packMeta = new CookiePack
            {
                Label         = packName,
                Slug          = $"filtered-{SelectedProfileRow?.Name ?? "profile"}-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                Domains       = domains,
                CookiesCount  = filteredCookies.Count,
                AgeDays       = 0,
                CaptchaRate   = 0,
            };

            // Persist to the cookie_packs table via ICookiePackService.UpsertAsync.
            var packId = await _packs.UpsertAsync(packMeta, payload);

            _log.LogInformation(
                "Filtered cookies saved as pack #{PackId}: '{Label}' ({Count} cookies)",
                packId, packMeta.Label, filteredCookies.Count);

            await _dialogs.ConfirmAsync(
                $"Saved as cookie pack '{packMeta.Label}'",
                $"Pack #{packId}\n" +
                $"Cookies: {filteredCookies.Count}\n" +
                $"Domains: {domains.Count}\n\n" +
                "Open Cookie Pool from the sidebar to manage or rename your saved packs.",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Save filtered cookies as pack failed");
            await _dialogs.ConfirmAsync("Could not save as pack",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Snapshot detail viewer
    // ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenSnapshotAsync(SessionSnapshot? selected)
    {
        if (selected is null) return;
        try
        {
            var payload = await _sessions.GetPayloadAsync(selected.Id);
            if (payload is null)
            {
                await _dialogs.ConfirmAsync("Snapshot missing",
                    "The snapshot row exists but its payload is unreadable (deleted between list and fetch).",
                    "OK", ConfirmSeverity.Error);
                return;
            }
            // The dialog renders a read-only inspector. Implementing
            // it as a modal window keeps the page free of nested grids.
            var dlg = new Dialogs.SnapshotDetailDialog(selected, payload)
            {
                Owner = Application.Current?.MainWindow,
            };
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Open snapshot #{Id} failed", selected.Id);
            await _dialogs.ConfirmAsync("Could not open snapshot",
                ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Chrome import
    // ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task DiscoverChromeSourcesAsync()
    {
        try
        {
            var sources = await _chrome.DiscoverAsync();
            ChromeSources.Clear();
            foreach (var s in sources) ChromeSources.Add(s);
            SelectedChromeSource = ChromeSources.FirstOrDefault();
            ChromeImportStatus = sources.Count == 0
                ? "No Chromium-based browsers detected on this machine."
                : $"Detected {sources.Count} browser profile(s).";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chrome discovery failed");
            ChromeImportStatus = "Discovery failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task RunChromeImportAsync()
    {
        var src = SelectedChromeSource;
        var target = SelectedProfileRow?.Name;
        if (src is null)
        {
            await _dialogs.ConfirmAsync(
                "Pick a source",
                "No Chromium browser profile selected — click Detect to scan the machine, or pick one from the dropdown.",
                "OK", ConfirmSeverity.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(target))
        {
            await _dialogs.ConfirmAsync(
                "Pick a target profile",
                "Select a Ghost Shell profile in the sidebar — the imported cookies are saved as a snapshot under it.",
                "OK", ConfirmSeverity.Warning);
            return;
        }
        if (_runner.ActiveProfileNames.Contains(target))
        {
            await _dialogs.ConfirmAsync(
                "Profile is currently running",
                $"Stop '{target}' first — the import writes a snapshot that's auto-restored on the NEXT launch.",
                "OK", ConfirmSeverity.Warning);
            return;
        }
        IsChromeImporting = true;
        ChromeImportStatus = "Importing… 0s";

        // Phase 70 — UI-thread responsiveness. The previous code awaited
        // ImportAsync directly on the dispatcher; ChromeImporter's
        // internal SQLite reads + DPAPI calls block synchronously, so
        // the UI froze for the duration of the import (typically 3-6s).
        // Two improvements:
        //   • A DispatcherTimer ticks every 500ms and rewrites the
        //     status with elapsed seconds + a stage hint so the user
        //     sees the import is alive, not hung.
        //   • The import call itself is wrapped in Task.Run so its
        //     synchronous SQLite reads happen on a thread-pool worker.
        //     The dispatcher stays free for the timer + any other UI
        //     work (tray badge, log tail, etc).
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var stageHints = new[]
        {
            "reading Local State key",
            "copying Cookies DB",
            "decrypting cookie values",
            "reading History DB",
            "filtering sensitive domains",
            "saving snapshot",
        };
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            // Pick a stage hint based on elapsed time so the message
            // tracks roughly with where the importer actually is. It's
            // best-effort theatre -- accurate enough for "the app is
            // working, just wait".
            var sec = (int)stopwatch.Elapsed.TotalSeconds;
            var stageIdx = Math.Min(sec / 1, stageHints.Length - 1);
            ChromeImportStatus = $"Importing… {sec}s · {stageHints[stageIdx]}";
        };
        timer.Start();

        try
        {
            var opts = new ChromeImportOptions
            {
                Source                = src,
                TargetProfileName     = target,
                ImportCookies         = ChromeImportCookies,
                HistoryDays           = Math.Max(0, ChromeHistoryDays),
                MaxUrls               = Math.Max(0, ChromeMaxUrls),
                SkipSensitiveDomains  = ChromeSkipSensitive,
            };
            // Off-thread the import so the dispatcher stays responsive
            // while ChromeImporter's blocking SQLite reads run.
            var result = await Task.Run(() => _chrome.ImportAsync(opts));
            timer.Stop();
            stopwatch.Stop();
            ChromeImportStatus = result.Summary;
            await ReloadSelectedAsync();

            var sev = result.Warnings.Count > 0 || result.CookiesUndecryptable > 0
                ? ConfirmSeverity.Warning
                : ConfirmSeverity.Success;
            var msg = result.Summary;
            if (result.Warnings.Count > 0)
                msg += "\n\nWarnings:\n  • " + string.Join("\n  • ", result.Warnings);
            await _dialogs.ConfirmAsync("Chrome import", msg, "OK", sev);
            // Pin the user-visible status to "Done" so the previous
            // "Importing…" doesn't linger on the page after the
            // confirmation closes.
            ChromeImportStatus = result.Summary;
        }
        catch (Exception ex)
        {
            timer.Stop();
            stopwatch.Stop();
            _log.LogError(ex, "Chrome import failed");
            // Phase 70 — recognise the "Chrome is running" footprint and
            // surface an actionable message. SQLite Error 14 ("unable to
            // open database file") at the source-direct fallback means
            // Chrome holds an exclusive lock on Cookies/History; closing
            // Chrome is the only fix.
            var msg = ex.Message ?? "";
            string friendly;
            if (msg.Contains("unable to open database file", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("database is locked", StringComparison.OrdinalIgnoreCase))
            {
                friendly =
                    "Chrome is running and has its data files locked.\n\n" +
                    "Close ALL Chrome windows (also check Task Manager for stray chrome.exe processes) " +
                    "and retry the import.\n\n" +
                    "Original error: " + msg;
            }
            else
            {
                friendly = msg;
            }
            ChromeImportStatus = "Import failed: " + msg;
            await _dialogs.ConfirmAsync(
                "Chrome import failed", friendly, "OK", ConfirmSeverity.Error);
        }
        finally
        {
            // Belt-and-braces: stop the timer + watch even if try/catch
            // exited via an unexpected path. Calling Stop() on an
            // already-stopped DispatcherTimer is a no-op.
            try { timer.Stop(); } catch { /* ignore */ }
            try { stopwatch.Stop(); } catch { /* ignore */ }
            IsChromeImporting = false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// "22h ago" / "5m ago" / "just now". Same shape as legacy.
    /// </summary>
    public static string HumanizeAgo(DateTime when)
    {
        // Treat unspecified-kind timestamps as UTC (the DB writes
        // UTC ISO strings; Dapper round-trips them as Unspecified).
        var utc = when.Kind switch
        {
            DateTimeKind.Utc          => when,
            DateTimeKind.Local        => when.ToUniversalTime(),
            _                         => DateTime.SpecifyKind(when, DateTimeKind.Utc),
        };
        var delta = DateTime.UtcNow - utc;
        if (delta.TotalSeconds < 30)  return "just now";
        if (delta.TotalMinutes < 60)  return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours   < 24)  return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays    < 30)  return $"{(int)delta.TotalDays}d ago";
        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }
}

// ─── Row models ──────────────────────────────────────────────────────

public sealed partial class ProfileRow : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>
    /// True if a regular monitor run is in progress (from
    /// IProfileRunner). The Run-warmup button is disabled in this state
    /// because the two would fight for the same Chromium user-data-dir.
    /// </summary>
    [ObservableProperty] private bool _isRunning;

    /// <summary>True if a warmup is in progress for this profile.</summary>
    [ObservableProperty] private bool _isWarmupRunning;

    /// <summary>
    /// Multi-select flag — driven by the per-row checkbox. The Run-
    /// warmup button targets the union of every checked row when at
    /// least one is checked, otherwise falls back to the focused row.
    /// Mirrors the Profiles page bulk-select pattern.
    /// </summary>
    [ObservableProperty] private bool _isChecked;

    /// <summary>
    /// Convenience flag for the sidebar's "live" dot — green when
    /// either kind of activity is on.
    /// </summary>
    public bool IsLive => IsRunning || IsWarmupRunning;

    partial void OnIsRunningChanged(bool value)       => OnPropertyChanged(nameof(IsLive));
    partial void OnIsWarmupRunningChanged(bool value) => OnPropertyChanged(nameof(IsLive));
}

/// <summary>
/// Preset card item. <see cref="IsSelected"/> drives the selected-card
/// visual via a DataTrigger in the View; the parent VM owns the
/// "exactly-one-selected" invariant.
///
/// Per-preset hue: each preset gets a distinct accent colour so the
/// card grid reads as a palette at a glance. The mapping picks one of
/// the existing sidebar Hue brushes (Colors.xaml) so the page feels
/// consistent with the rest of the app's colour language.
/// </summary>
public sealed partial class PresetCard : ObservableObject
{
    public required string Id          { get; init; }
    public required string Label       { get; init; }
    public required string Description { get; init; }
    public required int    SiteCount   { get; init; }

    [ObservableProperty] private bool _isSelected;

    /// <summary>
    /// Per-card accent (icon + selected-state border + colour stripe).
    /// Resolved at construction time from the app's Hue palette so the
    /// brush is a real <see cref="Brush"/> instance ready for binding.
    /// </summary>
    public Brush AccentBrush { get; init; } = Brushes.Gray;

    /// <summary>Emoji glyph used as the card's at-a-glance icon.</summary>
    public string Icon { get; init; } = "🌐";
}

/// <summary>
/// Read-only view of a <see cref="CookieEntry"/> used by the Cookies
/// tab grid. We materialise this so converters don't have to run for
/// every cell, and we can sort/filter on already-formatted strings.
/// </summary>
public sealed record CookieRowView
{
    public required string Domain   { get; init; }
    public required string Name     { get; init; }
    public required string Value    { get; init; }
    public required string Path     { get; init; }
    public required bool   Secure   { get; init; }
    public required bool   HttpOnly { get; init; }
    public string? SameSite { get; init; }
    public long?  ExpiresUnixSec { get; init; }

    /// <summary>Pretty expiry text — "session", "in 12 days", or
    /// "expired 2024-12-30".</summary>
    public string ExpiryDisplay
    {
        get
        {
            if (ExpiresUnixSec is not { } sec || sec <= 0) return "session";
            var t = DateTimeOffset.FromUnixTimeSeconds(sec).UtcDateTime;
            var delta = t - DateTime.UtcNow;
            if (delta.TotalSeconds < 0)  return $"expired {t:yyyy-MM-dd}";
            if (delta.TotalDays > 365)   return $"in {(int)(delta.TotalDays / 365)}y";
            if (delta.TotalDays > 30)    return $"in {(int)(delta.TotalDays / 30)}mo";
            if (delta.TotalDays > 1)     return $"in {(int)delta.TotalDays}d";
            if (delta.TotalHours > 1)    return $"in {(int)delta.TotalHours}h";
            return $"in {Math.Max(1, (int)delta.TotalMinutes)}m";
        }
    }

    /// <summary>Truncated value for the grid — long cookies (e.g. JWTs)
    /// would otherwise blow up row heights.</summary>
    public string ValueDisplay
        => Value.Length <= 60 ? Value : Value[..57] + "…";

    public static CookieRowView From(CookieEntry c) => new()
    {
        Domain         = c.Domain,
        Name           = c.Name,
        Value          = c.Value,
        Path           = c.Path,
        Secure         = c.Secure,
        HttpOnly       = c.HttpOnly,
        SameSite       = c.SameSite,
        ExpiresUnixSec = c.ExpiresUnixSec,
    };
}

/// <summary>
/// Shape the Warmup-history DataGrid binds to. Materialised once at
/// reload time — saves the converters from running on every cell.
/// </summary>
public sealed record WarmupHistoryRow
{
    public long Id { get; init; }
    public required DateTime StartedAt { get; init; }
    public required string PresetId { get; init; }
    public required string Trigger { get; init; }
    public required string Sites { get; init; }       // "7/7"
    public required string Duration { get; init; }    // "2m 15s"
    public required string Status { get; init; }
    public required Brush  StatusBrush { get; init; }
    public string? Notes { get; init; }

    public static WarmupHistoryRow From(
        WarmupRun r, Brush ok, Brush warn, Brush err, Brush dim) => new()
    {
        Id        = r.Id,
        StartedAt = r.StartedAt,
        PresetId  = r.Preset ?? "?",
        Trigger   = r.Trigger,
        Sites     = $"{r.SitesSucceeded}/{r.SitesPlanned}",
        Duration  = FormatDuration(r.DurationSec),
        Status    = r.Status,
        StatusBrush = r.Status switch
        {
            "ok"      => ok,
            "partial" => warn,
            "failed"  => err,
            "running" => dim,
            _         => dim,
        },
        Notes = r.Notes,
    };

    private static string FormatDuration(double? sec)
    {
        if (sec is not { } s) return "—";
        if (s < 60)   return $"{s:F0}s";
        if (s < 3600) return $"{(int)(s / 60)}m {((int)s) % 60}s";
        return $"{(int)(s / 3600)}h {((int)s) % 3600 / 60}m";
    }
}

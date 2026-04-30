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

public sealed partial class ProxyViewModel : BaseViewModel
{
    private readonly IProxyService _proxies;
    private readonly IProxyHealthService _health;
    private readonly IProxyTester _tester;
    private readonly IDialogService _dialogs;
    private readonly ILogger<ProxyViewModel> _log;

    public ProxyViewModel(
        IProxyService proxies,
        IProxyHealthService health,
        IProxyTester tester,
        IDialogService dialogs,
        ILogger<ProxyViewModel> log)
    {
        _proxies = proxies;
        _health  = health;
        _tester  = tester;
        _dialogs = dialogs;
        _log     = log;
    }

    public ObservableCollection<Proxy> Items { get; } = new();
    public ObservableCollection<ProxyHealthEvent> TimelineEvents { get; } = new();
    public ObservableCollection<TimelineRowVm> TimelineRows { get; } = new();

    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private bool _isTimelineEmpty = true;
    [ObservableProperty] private string _statusBar = "";
    [ObservableProperty] private DateTime _timelineRangeStart;
    [ObservableProperty] private DateTime _timelineRangeEnd;

    public override async Task OnNavigatedToAsync() => await ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _log.LogDebug("Proxy: reload");
        IsBusy = true;
        try
        {
            Items.Clear();
            foreach (var p in await _proxies.ListAsync()) Items.Add(p);
            IsEmpty = Items.Count == 0;

            // Health events for the last 7 days — used by the timeline
            // widget at the bottom of the page.
            var rangeStart = DateTime.UtcNow.AddDays(-7);
            var rangeEnd   = DateTime.UtcNow;
            TimelineRangeStart = rangeStart;
            TimelineRangeEnd   = rangeEnd;

            TimelineEvents.Clear();
            foreach (var ev in await _health.ListAsync(rangeStart))
                TimelineEvents.Add(ev);

            // Group events by proxy and only render lanes for proxies
            // that actually have events. Untested rows just clutter
            // the page — they're already visible in the table above.
            TimelineRows.Clear();
            var byProxy = TimelineEvents
                .GroupBy(ev => ev.ProxySlug)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var p in Items)
            {
                if (!byProxy.TryGetValue(p.Slug, out var evs)) continue;
                TimelineRows.Add(new TimelineRowVm
                {
                    DisplayLine = p.LastIp ?? p.Name ?? p.Slug,
                    SubLine     = BuildSubLine(p, evs),
                    Events      = evs,
                });
            }
            IsTimelineEmpty = TimelineRows.Count == 0;

            UpdateStatusBar();
            _log.LogInformation(
                "Proxies loaded: {Count} item(s), {Events} events, {Lanes} lanes on timeline",
                Items.Count, TimelineEvents.Count, TimelineRows.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Proxy list failed");
        }
        finally { IsBusy = false; }
    }

    private static string BuildSubLine(Proxy p, IReadOnlyList<ProxyHealthEvent> evs)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(p.Country)) parts.Add(p.Country);
        var rotations = evs.Count(e => e.Kind == ProxyHealthEventKind.Rotation);
        var captchas  = evs.Count(e => e.Kind == ProxyHealthEventKind.Captcha);
        if (rotations > 0) parts.Add($"{rotations} rot");
        if (captchas  > 0) parts.Add($"{captchas} cap");
        return parts.Count == 0 ? "—" : string.Join(" · ", parts);
    }

    private void UpdateStatusBar()
    {
        var total    = Items.Count;
        var active   = Items.Count(i => i.Health == ProxyHealth.Healthy);
        var errors   = Items.Count(i => i.Health == ProxyHealth.Critical);
        var untested = Items.Count(i => i.Health == ProxyHealth.Unknown);
        StatusBar = $"{total} total · {active} active · {errors} errors · {untested} untested";
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var draft = await _dialogs.ShowProxyEditorAsync(null);
        if (draft is null) return;

        try
        {
            await _proxies.CreateAsync(draft);
            await _health.RecordAsync(new ProxyHealthEvent
            {
                ProxySlug = draft.Slug,
                Kind      = ProxyHealthEventKind.FirstSeen,
                At        = DateTime.UtcNow,
            });
            _log.LogInformation("Proxy '{Slug}' created via UI", draft.Slug);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to create proxy '{Slug}'", draft.Slug);
            await _dialogs.ConfirmAsync(
                "Could not create proxy",
                $"{ex.Message}\n\nCheck that the slug is unique.",
                "OK",
                ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task BulkImportAsync()
    {
        var picks = await _dialogs.ShowBulkImportProxiesAsync();
        if (picks is null || picks.Count == 0) return;

        // Translate ParsedProxy → Proxy (slug auto-generated from
        // host:port if no name was given). Same model as the legacy
        // bulk-import endpoint.
        var rows = picks.Select((p, i) => new Proxy
        {
            Slug = $"imported-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{i}",
            Name = $"{p.Host}:{p.Port}",
            Url  = p.Url ?? "",
        }).ToList();

        try
        {
            var result = await _proxies.BulkCreateAsync(rows);
            foreach (var p in result.Created)
            {
                await _health.RecordAsync(new ProxyHealthEvent
                {
                    ProxySlug = p.Slug,
                    Kind      = ProxyHealthEventKind.FirstSeen,
                    At        = DateTime.UtcNow,
                });
            }
            _log.LogInformation("Bulk import: {Created} created, {Skipped} skipped (duplicates)",
                result.Created.Count, result.Skipped.Count);
            await ReloadAsync();

            await _dialogs.ConfirmAsync(
                "Import complete",
                $"Created: {result.Created.Count}\nSkipped (duplicates): {result.Skipped.Count}",
                "OK");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bulk import failed");
            await _dialogs.ConfirmAsync("Import failed", ex.Message, "OK", ConfirmSeverity.Error);
        }
    }

    /// <summary>
    /// Concurrency cap for parallel proxy probes. 8 is a sane
    /// default — high enough that 100 proxies finish in ~3s with the
    /// stub, low enough that real HTTP probes (Phase 3) won't hit
    /// per-host rate limits or trip a SOCKS pool's connection cap.
    /// </summary>
    private const int TestAllConcurrency = 8;

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (Items.Count == 0) return;

        var snapshot = Items.ToList();
        _log.LogInformation("Test-all: probing {Count} proxies (concurrency={Conc})",
            snapshot.Count, TestAllConcurrency);

        IsBusy = true;
        var ok = 0;
        var fail = 0;
        var completed = 0;
        var total = snapshot.Count;

        try
        {
            // Throttle parallelism with a semaphore — Parallel.ForEachAsync
            // would also work but this gives us cleaner control over the
            // "completed" counter for status updates.
            using var sem = new SemaphoreSlim(TestAllConcurrency);
            var tasks = snapshot.Select(async p =>
            {
                await sem.WaitAsync();
                try
                {
                    var r = await _tester.TestAsync(p);
                    await _proxies.RecordTestResultAsync(p.Slug, r);
                    if (r.Ok) Interlocked.Increment(ref ok);
                    else      Interlocked.Increment(ref fail);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref fail);
                    _log.LogWarning(ex, "Test failed for '{Slug}'", p.Slug);
                }
                finally
                {
                    var done = Interlocked.Increment(ref completed);
                    // Throttle StatusBar updates — flushing 74 strings to
                    // the UI thread for every completion is itself a
                    // latency hog. Update every 5th and on the last one.
                    if (done == total || done % 5 == 0)
                    {
                        Application.Current?.Dispatcher.BeginInvoke(() =>
                            StatusBar = $"Testing… {done} / {total} ({ok} ok · {fail} failed)");
                    }
                    sem.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            // Single reload at the end — avoids re-rendering the whole
            // table 74 times during the run.
            await ReloadAsync();
            StatusBar = $"Test-all done: {ok} ok · {fail} failed";
            _log.LogInformation("Test-all done: {Ok} ok / {Fail} fail", ok, fail);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task EditAsync(Proxy? selected)
    {
        if (selected is null) return;

        // Wire "Rotate IP now" — Phase 2 ships a stub that just logs
        // a health event so the timeline gets a fresh dot. Real HTTP
        // call to the rotation URL lands in Phase 3 with the runtime.
        var edited = await _dialogs.ShowProxyEditorAsync(selected,
            onRotateNow: async (slug, rotateUrl) =>
            {
                _log.LogInformation(
                    "Manual rotation requested for '{Slug}' → {Url} (stub)",
                    slug, rotateUrl);
                await _health.RecordAsync(new ProxyHealthEvent
                {
                    ProxySlug = slug,
                    Kind      = ProxyHealthEventKind.Rotation,
                    At        = DateTime.UtcNow,
                    Detail    = "Manual rotation (stub)",
                });
                return "Rotation triggered. Real HTTP call lands in Phase 3.";
            });
        if (edited is null) return;

        try
        {
            await _proxies.UpdateAsync(edited);
            _log.LogInformation("Proxy '{Slug}' updated via UI", edited.Slug);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to update proxy '{Slug}'", edited.Slug);
        }
    }

    [RelayCommand]
    private async Task TestOneAsync(Proxy? selected)
    {
        if (selected is null) return;
        try
        {
            var r = await _tester.TestAsync(selected);
            await _proxies.RecordTestResultAsync(selected.Slug, r);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Single-proxy test failed for '{Slug}'", selected.Slug);
        }
    }

    /// <summary>
    /// One lane on the Health Timeline. Owns its own immutable
    /// event list so the lane control can render without back-
    /// referencing the page-level dictionary.
    /// </summary>
    public sealed class TimelineRowVm
    {
        public required string DisplayLine { get; init; }
        public required string SubLine { get; init; }
        public required IReadOnlyList<ProxyHealthEvent> Events { get; init; }
    }

    [RelayCommand]
    private async Task DeleteAsync(Proxy? selected)
    {
        if (selected is null) return;

        var displayName = selected.Name ?? selected.Slug;
        var ok = await _dialogs.ConfirmAsync(
            $"Delete proxy '{displayName}'?",
            "Profiles bound to this proxy will keep the slug reference, " +
            "but launches will fail until they're rebound. Diagnostics " +
            "history is kept.",
            "Delete");
        if (!ok) return;

        try
        {
            await _proxies.DeleteAsync(selected.Slug);
            _log.LogInformation("Proxy '{Slug}' deleted via UI", selected.Slug);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to delete proxy '{Slug}'", selected.Slug);
        }
    }
}

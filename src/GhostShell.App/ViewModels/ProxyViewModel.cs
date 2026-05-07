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

        // Phase 61b — live status updates during Test-all.
        // Build slug→index map BEFORE we kick off the parallel probes so
        // we can replace each row in-place as its result lands. The
        // ObservableCollection's setter on an indexer fires
        // INotifyCollectionChanged.Replace which the DataGrid handles by
        // re-rendering JUST that row — no full table redraw.
        var slugToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Items.Count; i++) slugToIndex[Items[i].Slug] = i;

        // Mark every row as "queued" so the user immediately sees the
        // test kicked off (otherwise the table looks frozen for the
        // first few seconds while the semaphore fills up).
        for (int i = 0; i < Items.Count; i++)
        {
            var p = Items[i];
            Items[i] = WithHealth(p, ProxyHealth.Unknown, latencyMs: null,
                                  lastCheckedAt: DateTime.UtcNow);
        }

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

                    // Phase 61b — pull the freshly-saved row back from DB
                    // (it now has Country/City/Latency/Health populated)
                    // and swap into the ObservableCollection by slug. The
                    // DataGrid binding sees the index-set and re-renders
                    // ONLY that row, so the user watches statuses turn
                    // green/red live as each probe finishes.
                    try
                    {
                        var refreshed = await _proxies.GetAsync(p.Slug);
                        if (refreshed is not null
                            && slugToIndex.TryGetValue(p.Slug, out var idx))
                        {
                            // Marshal to UI thread — ObservableCollection
                            // mutations from background tasks blow up
                            // with NotSupportedException otherwise.
                            Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                if (idx < Items.Count) Items[idx] = refreshed;
                            });
                        }
                    }
                    catch (Exception lex)
                    {
                        _log.LogDebug(lex, "Live-row refresh failed for '{Slug}'", p.Slug);
                    }
                    // Phase 61 — if the prober detected a working scheme
                    // that differs from the URL the user stored, rewrite
                    // the URL with the correct scheme. Otherwise the
                    // browser launch keeps using the wrong protocol and
                    // dies with ERR_PROXY_CONNECTION_FAILED even though
                    // the test passed.
                    if (r.Ok && !string.IsNullOrEmpty(r.DetectedScheme))
                    {
                        var corrected = MaybeCorrectUrlScheme(p.Url, r.DetectedScheme);
                        if (corrected is not null && !string.Equals(corrected, p.Url, StringComparison.Ordinal))
                        {
                            _log.LogInformation(
                                "Proxy '{Slug}': auto-corrected URL scheme {Old} → {New}",
                                p.Slug, p.Url, corrected);
                            try
                            {
                                // Build a new Proxy instance — the model is a
                                // class with init-only setters, which is why
                                // `with` doesn't compile here. Copying every
                                // diagnostic-state field preserves last-test
                                // metadata (geo, ASN, latency) so the row
                                // doesn't visually reset to "unknown".
                                var fixedProxy = new Proxy
                                {
                                    Slug             = p.Slug,
                                    Name             = p.Name,
                                    Url              = corrected,
                                    IsRotating       = p.IsRotating,
                                    RotationApiUrl   = p.RotationApiUrl,
                                    RotationProvider = p.RotationProvider,
                                    RotationApiKey   = p.RotationApiKey,
                                    IsDefault        = p.IsDefault,
                                    Notes            = p.Notes,
                                    LastIp           = p.LastIp,
                                    Country          = p.Country,
                                    CountryCode      = p.CountryCode,
                                    City             = p.City,
                                    Asn              = p.Asn,
                                    Isp              = p.Isp,
                                    IpType           = p.IpType,
                                    LatencyMs        = p.LatencyMs,
                                    Health           = p.Health,
                                    LastCheckedAt    = p.LastCheckedAt,
                                    ProfileCount     = p.ProfileCount,
                                    CreatedAt        = p.CreatedAt,
                                    UpdatedAt        = DateTime.UtcNow,
                                };
                                await _proxies.UpdateAsync(fixedProxy);
                            }
                            catch (Exception uex)
                            {
                                _log.LogWarning(uex,
                                    "Could not persist scheme auto-correction for '{Slug}'", p.Slug);
                            }
                        }
                    }
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

        // Phase 71hh — REAL "Rotate IP now". Pre-fix this was a stub
        // that only wrote a fake health-timeline event without
        // actually hitting the rotation URL. The button in the
        // editor was therefore lying to the user — the asocks /
        // smartproxy / etc. dashboard kept the same exit IP and
        // Google captcha state persisted. Now we mirror what
        // RealProfileRunner does at auto-rotate-on-launch time:
        // fire a simple HTTP GET against the rotation URL, wait a
        // 2-second settle, return human-readable status. Failures
        // are surfaced verbatim so the user can see WHY rotation
        // didn't take (timeout / 401 / 5xx / unreachable).
        var edited = await _dialogs.ShowProxyEditorAsync(selected,
            onRotateNow: async (slug, rotateUrl) =>
            {
                _log.LogInformation(
                    "Manual rotation requested for '{Slug}' → {Url}",
                    slug, rotateUrl);
                string detail;
                bool ok;
                try
                {
                    using var http = new System.Net.Http.HttpClient
                    {
                        Timeout = TimeSpan.FromSeconds(15),
                    };
                    var response = await http.GetAsync(rotateUrl);
                    response.EnsureSuccessStatusCode();
                    // 2-second settle so the upstream proxy registers
                    // the new exit IP before the next launch picks it
                    // up (matches the auto-rotate-on-launch path).
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    ok = true;
                    detail = $"Manual rotation OK ({(int)response.StatusCode} {response.ReasonPhrase})";
                }
                catch (Exception ex)
                {
                    ok = false;
                    detail = $"Manual rotation FAILED: {ex.GetType().Name} — {ex.Message}";
                    _log.LogWarning(ex,
                        "Manual rotation failed for '{Slug}' → {Url}", slug, rotateUrl);
                }
                await _health.RecordAsync(new ProxyHealthEvent
                {
                    ProxySlug = slug,
                    Kind      = ok ? ProxyHealthEventKind.Rotation : ProxyHealthEventKind.Burn,
                    At        = DateTime.UtcNow,
                    Detail    = detail,
                });
                return ok
                    ? "✓ Rotation triggered. New exit IP should be live in ~2s."
                    : detail;
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
    /// Phase 61b — clone a Proxy with overridden health-state fields.
    /// Used by Test-all to mark all rows as "queued" before the parallel
    /// probe loop kicks off. The ObservableCollection's index-set fires
    /// a Replace notification per row so the DataGrid re-renders the
    /// status icon/latency cell without a full table redraw.
    /// </summary>
    private static Proxy WithHealth(
        Proxy p, ProxyHealth health, int? latencyMs, DateTime? lastCheckedAt)
    {
        return new Proxy
        {
            Slug             = p.Slug,
            Name             = p.Name,
            Url              = p.Url,
            IsRotating       = p.IsRotating,
            RotationApiUrl   = p.RotationApiUrl,
            RotationProvider = p.RotationProvider,
            RotationApiKey   = p.RotationApiKey,
            IsDefault        = p.IsDefault,
            Notes            = p.Notes,
            LastIp           = p.LastIp,
            Country          = p.Country,
            CountryCode      = p.CountryCode,
            City             = p.City,
            Asn              = p.Asn,
            Isp              = p.Isp,
            IpType           = p.IpType,
            LatencyMs        = latencyMs,
            Health           = health,
            LastCheckedAt    = lastCheckedAt,
            ProfileCount     = p.ProfileCount,
            CreatedAt        = p.CreatedAt,
            UpdatedAt        = p.UpdatedAt,
        };
    }

    /// <summary>
    /// Phase 61 — if the prober detected that the proxy actually speaks
    /// a different scheme than the URL declares (e.g. URL says http://
    /// but the endpoint only spoke SOCKS5), return the URL with the
    /// scheme rewritten. Returns null if the URL can't be parsed or the
    /// detected scheme matches the URL's scheme already (no-op).
    ///
    /// Edge cases:
    ///   • If <paramref name="detectedScheme"/> is "tcp" — that's the
    ///     reachability-only attempt, not a real protocol. Return null.
    ///   • If URL scheme is "https" and detected is "http", we DON'T
    ///     downgrade — the user's intent was probably a CONNECT-style
    ///     proxy that happened to also be reachable plain. Bail safely.
    /// </summary>
    private static string? MaybeCorrectUrlScheme(string url, string detectedScheme)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(detectedScheme))
            return null;
        if (detectedScheme == "tcp") return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u))
        {
            // schemeless host:port — make it the detected scheme
            return $"{detectedScheme}://{url.Trim()}";
        }
        var current = u.Scheme.ToLowerInvariant();
        if (current == detectedScheme) return null;
        // Don't auto-downgrade https:// to http:// — preserve user intent.
        if (current == "https" && detectedScheme == "http") return null;
        var b = new UriBuilder(u) { Scheme = detectedScheme };
        // UriBuilder forces a default port for the new scheme; restore
        // the original port if present so we don't accidentally rewrite
        // the endpoint location.
        if (u.Port > 0) b.Port = u.Port;
        return b.Uri.ToString().TrimEnd('/');
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

    /// <summary>
    /// Phase 70 — bulk delete N selected proxies. The DataGrid binds
    /// its <see cref="System.Collections.IList"/> SelectedItems via
    /// CommandParameter. We snapshot to a typed list FIRST because
    /// Items.Clear() during ReloadAsync would invalidate the live
    /// selection enumerator. Falls back to single-row behaviour if
    /// only one is picked, with a friendlier prompt.
    /// </summary>
    [RelayCommand]
    private async Task BulkDeleteAsync(System.Collections.IList? selected)
    {
        if (selected is null) return;
        var picks = selected.OfType<Proxy>().ToList();
        if (picks.Count == 0) return;

        var title = picks.Count == 1
            ? $"Delete proxy '{picks[0].Name ?? picks[0].Slug}'?"
            : $"Delete {picks.Count} proxies?";
        var body = picks.Count == 1
            ? "Profiles bound to this proxy will keep the slug reference, " +
              "but launches will fail until they're rebound. Diagnostics " +
              "history is kept."
            : "Profiles bound to any of these proxies will keep the slug " +
              "reference, but launches will fail until they're rebound. " +
              "Diagnostics history is kept.\n\n" +
              "Will delete:\n  • " +
              string.Join("\n  • ",
                  picks.Take(15).Select(p => p.Name ?? p.Slug)) +
              (picks.Count > 15 ? $"\n  • …and {picks.Count - 15} more" : "");

        var ok = await _dialogs.ConfirmAsync(title, body, "Delete",
            picks.Count > 1 ? ConfirmSeverity.Danger : ConfirmSeverity.Neutral);
        if (!ok) return;

        // Sequential delete — IProxyService.DeleteAsync is per-row. We
        // track failures so a single bad row doesn't abort the rest.
        var failed = new List<(string slug, string err)>();
        foreach (var p in picks)
        {
            try { await _proxies.DeleteAsync(p.Slug); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to delete proxy '{Slug}' in bulk pass", p.Slug);
                failed.Add((p.Slug, ex.Message));
            }
        }
        var deleted = picks.Count - failed.Count;
        _log.LogInformation(
            "Bulk delete: {Done}/{Total} proxies deleted ({Failed} failed)",
            deleted, picks.Count, failed.Count);
        await ReloadAsync();

        if (failed.Count > 0)
        {
            var detail = string.Join("\n  • ",
                failed.Take(10).Select(f => $"{f.slug}: {f.err}"));
            if (failed.Count > 10) detail += $"\n  • …and {failed.Count - 10} more";
            await _dialogs.ConfirmAsync(
                "Bulk delete — partial",
                $"Deleted {deleted} of {picks.Count}. Errors:\n  • {detail}",
                "OK", ConfirmSeverity.Warning);
        }
    }
}

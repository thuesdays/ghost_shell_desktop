// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using System.Text.Json;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Real <see cref="IWarmupService"/>. Owns the per-warmup browser
/// session lifecycle and the per-site visitation loop. Mirrors
/// legacy <c>ghost_shell/session/warmup.py</c> behaviour:
///
/// 1. Pick sites via <see cref="PresetCatalog.PickSites"/> (geo-
///    filtered if a target country is configured — Phase 6+).
/// 2. Launch a browser via <see cref="IBrowserLauncher"/> bound to
///    the profile.
/// 3. For each site:
///      navigate → settle → consent-banner click → dwell → optional
///      scroll → record cookie deltas.
/// 4. On finish, save a snapshot via <see cref="ISessionService"/>
///    with trigger='auto_warmup' so the next regular launch
///    auto-restores the warmed cookies.
/// 5. UPDATE the warmup_runs row with terminal status / counts.
///
/// Concurrency:
///   • In-memory <see cref="_active"/> set blocks two warmups for the
///     same profile (StartAsync throws InvalidOperationException).
///   • The DB row is the persistent marker — IsRunningAsync also
///     consults it, so two app instances would still serialise via
///     the SQLite write lock.
///
/// Resilience:
///   • Per-site exceptions are caught, recorded, and the loop
///     continues. A single 404 / consent timeout doesn't abort the
///     whole warmup.
///   • Browser launch failure → row marked status='failed' with
///     notes containing the error.
///   • App crash mid-warmup → orphan row is swept on next startup
///     by <see cref="IWarmupHistoryService.SweepOrphansAsync"/>.
/// </summary>
public sealed class WarmupService : IWarmupService, IAsyncDisposable
{
    private readonly IBrowserLauncher _launcher;
    private readonly IProfileService _profiles;
    private readonly IProfileRunner _runner;
    private readonly ISessionService _sessions;
    private readonly IWarmupHistoryService _history;
    private readonly ILogger<WarmupService> _log;

    /// <summary>
    /// In-memory set of profile names that are currently running a
    /// warmup. Maps to a CancellationTokenSource so CancelAsync can
    /// interrupt the loop.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _orphanSweepDone;
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public WarmupService(
        IBrowserLauncher launcher,
        IProfileService profiles,
        IProfileRunner runner,
        ISessionService sessions,
        IWarmupHistoryService history,
        ILogger<WarmupService> log)
    {
        _launcher = launcher;
        _profiles = profiles;
        _runner   = runner;
        _sessions = sessions;
        _history  = history;
        _log      = log;
    }

    public IReadOnlyList<WarmupPresetDef> Presets => PresetCatalog.All;

    public IReadOnlySet<string> ActiveProfileNames =>
        new HashSet<string>(_active.Keys, StringComparer.OrdinalIgnoreCase);

    public event EventHandler? ActiveChanged;

    public async Task<long> StartAsync(
        string profileName, string presetId, int siteCount,
        string trigger = "manual", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(profileName))
            throw new ArgumentException("Profile name is required", nameof(profileName));

        // Lazy one-time orphan sweep: the row could legitimately have
        // status='running' from a previous app session that crashed.
        // Doing this in the ctor would be eager and async-in-ctor,
        // so we gate it here under a single-fire semaphore.
        await SweepOrphansOnceAsync(ct);

        var preset = PresetCatalog.Find(presetId)
            ?? throw new ArgumentException($"Unknown preset: {presetId}", nameof(presetId));

        // 1. Memory-level guard.
        if (_active.ContainsKey(profileName))
            throw new InvalidOperationException(
                $"Warmup already running for '{profileName}' in this app instance");

        // 2. Profile must exist.
        var profile = await _profiles.GetAsync(profileName, ct)
            ?? throw new InvalidOperationException(
                $"Profile '{profileName}' was not found");

        // 3. Profile must not be in a regular monitor run — they share
        //    the user-data-dir; two windows on one profile is undefined.
        if (_runner.ActiveProfileNames.Contains(profileName))
            throw new InvalidOperationException(
                $"Profile '{profileName}' has an active run — stop it before starting a warmup");

        // 4. DB-level guard (guards against a second app instance, if
        //    the user ever runs more than one).
        if (await _history.IsRunningAsync(profileName, ct))
            throw new InvalidOperationException(
                $"Profile '{profileName}' has a warmup row already in 'running' state");

        // Pick sites BEFORE inserting the row so we can record an
        // accurate sites_planned even if pick_sites returns fewer than
        // requested (e.g. country filter shrunk the bucket).
        var sites = PresetCatalog.PickSites(preset, siteCount);
        if (sites.Count == 0)
            throw new InvalidOperationException(
                $"Preset '{presetId}' produced 0 sites — check the catalog");

        var warmupId = await _history.StartAsync(
            profileName, presetId, sites.Count, trigger, ct);

        // Hand off to a fire-and-forget task. The CancellationToken
        // passed in here applies to the START call (e.g. UI shutting
        // down before the row insert finishes); the running loop has
        // its own CTS that CancelAsync flips.
        var loopCts = new CancellationTokenSource();
        _active[profileName] = loopCts;
        ActiveChanged?.Invoke(this, EventArgs.Empty);

        _ = Task.Run(async () =>
        {
            try
            {
                await RunLoopAsync(profile, preset, sites, warmupId, loopCts.Token);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Warmup #{Id} crashed unexpectedly", warmupId);
                try
                {
                    await _history.FinishAsync(
                        warmupId, "failed", 0, 0, 0,
                        notes: $"crashed: {ex.GetType().Name}: {ex.Message}",
                        sitesLogJson: "[]",
                        CancellationToken.None);
                }
                catch (Exception writeEx)
                {
                    _log.LogError(writeEx, "Warmup #{Id} crash-finish write also failed", warmupId);
                }
            }
            finally
            {
                _active.TryRemove(profileName, out _);
                loopCts.Dispose();
                ActiveChanged?.Invoke(this, EventArgs.Empty);
            }
        }, CancellationToken.None);

        return warmupId;
    }

    public Task<bool> CancelAsync(string profileName, CancellationToken ct = default)
    {
        if (_active.TryGetValue(profileName, out var cts))
        {
            try { cts.Cancel(); }
            catch { /* already cancelled */ }
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<WarmupRun>> ListHistoryAsync(
        string? profileName = null, int limit = 50, CancellationToken ct = default)
        => _history.ListAsync(profileName, limit, ct);

    public Task<WarmupRun?> GetLatestAsync(string profileName, CancellationToken ct = default)
        => _history.GetLatestAsync(profileName, ct);

    // ─────────────────────────────────────────────────────────────
    // Loop
    // ─────────────────────────────────────────────────────────────

    private async Task RunLoopAsync(
        Profile profile,
        WarmupPresetDef preset,
        IReadOnlyList<WarmupSite> sites,
        long warmupId,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var results = new List<WarmupSiteResult>(sites.Count);
        var visited = 0;
        var succeeded = 0;
        string? notes = null;

        IBrowserSession? session = null;
        try
        {
            _log.LogInformation(
                "Warmup #{Id}: launching browser for '{Profile}' (preset={Preset}, {N} sites)",
                warmupId, profile.Name, preset.Id, sites.Count);

            session = await _launcher.LaunchAsync(profile, ct);

            // Per-site loop. Per-site exceptions are caught here so a
            // single bad URL doesn't abort the whole warmup.
            for (var i = 0; i < sites.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var site = sites[i];
                var result = await VisitSiteAsync(session, site, i + 1, sites.Count, ct);
                results.Add(result);
                visited++;
                if (result.Ok) succeeded++;
            }

            // Auto-snapshot at the end so the next monitor run inherits
            // the warmed cookies. Trigger='auto_warmup' — already
            // documented in SessionSnapshot as a reserved code.
            try
            {
                var cookies = await session.GetCookiesAsync(ct);
                if (cookies.Count > 0)
                {
                    await _sessions.SaveAsync(
                        profile.Name,
                        new SessionPayload { Cookies = cookies, Storage = Array.Empty<StorageEntry>() },
                        runId: null,
                        trigger: "auto_warmup",
                        reason: $"warmup #{warmupId} ({preset.Id}, {succeeded}/{sites.Count} ok)",
                        ct: ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Warmup #{Id}: auto-snapshot failed — warmup row will still finalize",
                    warmupId);
                notes = $"auto-snapshot failed: {ex.Message}";
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Warmup #{Id} cancelled at site {N}/{Total}",
                warmupId, visited, sites.Count);
            notes = "cancelled by user";
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Warmup #{Id} loop failed at site {N}/{Total}",
                warmupId, visited, sites.Count);
            notes = $"loop error: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (session is not null)
            {
                try { await session.DisposeAsync(); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Warmup #{Id}: session dispose threw", warmupId);
                }
            }
        }

        // Status calculus mirrors legacy.
        string status;
        if (succeeded == sites.Count)        status = "ok";
        else if (succeeded > 0)              status = "partial";
        else                                 status = "failed";

        var duration = (DateTime.UtcNow - startedAt).TotalSeconds;
        var json = JsonSerializer.Serialize(results, JsonOpts);

        await _history.FinishAsync(
            warmupId, status, visited, succeeded, duration, notes, json, CancellationToken.None);
    }

    // ─────────────────────────────────────────────────────────────
    // Per-site visit
    // ─────────────────────────────────────────────────────────────

    private async Task<WarmupSiteResult> VisitSiteAsync(
        IBrowserSession session, WarmupSite site, int idx, int total, CancellationToken ct)
    {
        var t0 = DateTime.UtcNow;
        var cookiesBefore = 0;
        var cookiesAfter = 0;
        var consentClicked = false;
        var ok = false;
        string? error = null;

        try
        {
            // Cheap cookie count BEFORE so we can show a delta in the row-expand UI.
            try { cookiesBefore = (await session.GetCookiesAsync(ct)).Count; }
            catch { /* not fatal */ }

            _log.LogDebug("Warmup site {Idx}/{Total}: {Url}", idx, total, site.Url);

            await session.NavigateAsync(site.Url, ct);

            // Settle: a couple of seconds for the initial render.
            // Random within (1.0, 2.0) to avoid timing-fingerprint
            // synchronisation across multiple profiles in a fleet.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1000, 2000)), ct);

            // Best-effort consent-banner click. Boolean return value;
            // we record but don't fail the visit on a missing banner —
            // many sites simply don't have one anymore.
            consentClicked = await TryClickConsentAsync(session, ct);
            if (consentClicked)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(400, 900)), ct);
            }

            // Dwell with optional gentle scroll. Roll a uniform dwell
            // from the site's range so two warmups don't generate
            // identical timing fingerprints.
            var dwellSec = Random.Shared.Next(site.DwellMinSec, site.DwellMaxSec + 1);

            if (site.Scroll)
            {
                await GentleScrollAsync(session, dwellSec, ct);
            }
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(dwellSec), ct);
            }

            // Cookie count AFTER for the delta display.
            try { cookiesAfter = (await session.GetCookiesAsync(ct)).Count; }
            catch { /* not fatal */ }

            ok = true;
        }
        catch (OperationCanceledException)
        {
            // Cancellation propagates upward — but mark the entry as
            // not-OK with a clear note.
            error = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {Truncate(ex.Message, 200)}";
            _log.LogDebug(ex, "Warmup site {Idx}/{Total} failed: {Url}", idx, total, site.Url);
        }

        return new WarmupSiteResult
        {
            Url            = site.Url,
            Topic          = site.Topic,
            Ok             = ok,
            DurationMs     = (int)(DateTime.UtcNow - t0).TotalMilliseconds,
            CookiesBefore  = cookiesBefore,
            CookiesAfter   = cookiesAfter,
            ConsentClicked = consentClicked,
            Error          = error,
        };
    }

    /// <summary>
    /// Best-effort cookie-consent dismissal. Walks a list of CSS
    /// selectors common to OneTrust, Cookiebot, Quantcast, Google,
    /// plus a text-match fallback for "Accept all" / "I agree" /
    /// "Прийняти" / "Принять все" / "Согласен". Click happens
    /// in-page so it generates the same DOM events a real user would.
    /// Returns true if any selector matched and the click landed.
    /// </summary>
    private static async Task<bool> TryClickConsentAsync(
        IBrowserSession session, CancellationToken ct)
    {
        // The trick: do all the work in one round-trip JS. Returning
        // a single bool keeps the WebDriver chatter to a minimum.
        //
        // Strategy (in order of precision):
        //   1. Specific known selectors for the major CMP vendors.
        //   2. Generic "[data-action='accept']" patterns.
        //   3. Text-content match on <button> / <a> elements for
        //      "Accept all" / "I agree" / "OK" / "Прийняти" / "Согласен".
        //
        // Cyrillic is matched via String.includes (case-insensitive
        // via toLowerCase) — matches both "Прийняти" and "ПРИЙНЯТИ".
        const string Js = """
            (function() {
              try {
                var sels = [
                  '#onetrust-accept-btn-handler',
                  '#cookieAcceptAllButton',
                  'button[aria-label="Accept all"]',
                  'button[aria-label="Прийняти все"]',
                  'button[aria-label="Принять все"]',
                  'button[data-testid="uc-accept-all-button"]',
                  '.qc-cmp2-summary-buttons button[mode="primary"]',
                  'form[action*="consent.google"] button',
                  'button.fc-cta-consent',
                  'button[data-action="accept-all"]',
                  'button[data-cookieman-accept]',
                  'button[id*="accept"][id*="all"]',
                  '#cookie-banner button.btn-primary',
                  '#L2AGLb',
                  'button[aria-label*="Accept" i]'
                ];
                for (var i = 0; i < sels.length; i++) {
                  try {
                    var el = document.querySelector(sels[i]);
                    if (el && el.offsetParent !== null) {
                      el.click();
                      return true;
                    }
                  } catch (e) {}
                }
                // Text-content fallback. We scan visible <button> and
                // <a> elements only — "Accept" inside a <p> is noise.
                var phrases = [
                  'accept all', 'accept cookies', 'i agree', 'agree',
                  'allow all', 'got it', 'ok', 'accept',
                  'прийняти', 'погоджуюсь', 'погоджуюся',
                  'принять все', 'принять', 'согласен', 'согласна'
                ];
                var nodes = document.querySelectorAll('button, a[role="button"], [role="button"]');
                for (var j = 0; j < nodes.length; j++) {
                  var n = nodes[j];
                  if (n.offsetParent === null) continue;
                  var t = (n.innerText || n.textContent || '').trim().toLowerCase();
                  if (!t || t.length > 40) continue;
                  for (var k = 0; k < phrases.length; k++) {
                    if (t.indexOf(phrases[k]) !== -1) {
                      try { n.click(); return true; } catch (e) {}
                    }
                  }
                }
                return false;
              } catch (e) { return false; }
            })()
        """;
        try
        {
            var result = await session.ExecuteScriptAsync(Js, null, ct);
            return result is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Trickle scroll over <paramref name="totalSec"/> seconds. Each
    /// step nudges 300-700px down then waits 1.5-3.0s, giving a
    /// human-shaped scroll velocity profile. Caps at 8 steps to keep
    /// dwell from running away when a site has a short content list.
    /// </summary>
    private static async Task GentleScrollAsync(
        IBrowserSession session, int totalSec, CancellationToken ct)
    {
        var endsAt = DateTime.UtcNow.AddSeconds(totalSec);
        var step = 0;
        while (DateTime.UtcNow < endsAt && step < 8)
        {
            ct.ThrowIfCancellationRequested();
            var delta = Random.Shared.Next(300, 700);
            try
            {
                await session.ExecuteScriptAsync(
                    $"window.scrollBy({{top: {delta}, left: 0, behavior: 'smooth'}});",
                    null, ct);
            }
            catch { /* visit-non-fatal */ }
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(1500, 3000)), ct);
            step++;
        }
        // Tiny return-scroll at the end — looks more like someone
        // re-reading something they spotted.
        try
        {
            await session.ExecuteScriptAsync(
                "window.scrollBy({top: -200, left: 0, behavior: 'smooth'});",
                null, ct);
        }
        catch { /* whatever */ }
    }

    private async Task SweepOrphansOnceAsync(CancellationToken ct)
    {
        if (_orphanSweepDone) return;
        await _sweepGate.WaitAsync(ct);
        try
        {
            if (_orphanSweepDone) return;
            await _history.SweepOrphansAsync(ct);
            _orphanSweepDone = true;
        }
        finally { _sweepGate.Release(); }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max]);

    public async ValueTask DisposeAsync()
    {
        // Cancel everything still running so the engine fast-paths
        // cleanly. We do NOT await the running tasks — app shutdown
        // already has its own teardown sequence (AppShutdown.RunAsync)
        // that gives them a few seconds before killing chrome.exe.
        foreach (var kv in _active)
        {
            try { kv.Value.Cancel(); } catch { }
        }
        await Task.CompletedTask;
        _sweepGate.Dispose();
    }
}

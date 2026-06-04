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
/// 1. Pick sites via <see cref="PresetCatalog.PickSites"/>, geo-
///    filtered by the country derived from the profile's locale
///    (see ResolveTargetCountry — audit WARMUP-01).
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
        //
        // audit WARMUP-05: reserve the in-memory slot ATOMICALLY up
        // front, before any await, so two concurrent StartAsync calls
        // for the same profile (e.g. manual click + WarmupQualityMonitor
        // tick) can't both pass a check-then-act ContainsKey gate and
        // then both launch a browser on the same user-data-dir. We park
        // a placeholder CTS; the real loop CTS replaces it once we're
        // committed. Any failure path below MUST release the reservation
        // (see the try/catch wrapper) or the profile would be wedged
        // "busy" forever.
        var reservation = new CancellationTokenSource();
        if (!_active.TryAdd(profileName, reservation))
        {
            reservation.Dispose();
            throw new InvalidOperationException(
                $"Warmup already running for '{profileName}' in this app instance");
        }

        long warmupId;
        CancellationTokenSource loopCts;
        try
        {
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

            // audit WARMUP-01: derive a target country from the profile
            // so the geo-filter the docs promise actually runs. Without
            // this, PickSites was always called with targetCountry=null,
            // so a UA-locale profile could build an organic history
            // dominated by US-only sites — exactly the locale-inference
            // inconsistency WarmupSite.Countries exists to prevent.
            //
            // The region subtag of the profile's BCP-47 language tag
            // (e.g. "uk-UA" → "UA", "en-US" → "US") is the most reliable
            // in-process geo signal. Proxy egress geo would be stronger
            // but resolving ProxySlug → country lives in the proxy layer
            // (another file); if/when that's plumbed through it should be
            // preferred here. null = no resolvable country → unfiltered
            // pick, same as the prior behaviour (documented, not silent).
            var targetCountry = ResolveTargetCountry(profile);

            // Pick sites BEFORE inserting the row so we can record an
            // accurate sites_planned even if pick_sites returns fewer than
            // requested (e.g. country filter shrunk the bucket).
            var sites = PresetCatalog.PickSites(preset, siteCount, targetCountry);
            if (sites.Count == 0)
                throw new InvalidOperationException(
                    $"Preset '{presetId}' produced 0 sites — check the catalog");

            warmupId = await _history.StartAsync(
                profileName, presetId, sites.Count, trigger, ct);

            // Hand off to a fire-and-forget task. The CancellationToken
            // passed in here applies to the START call (e.g. UI shutting
            // down before the row insert finishes); the running loop has
            // its own CTS that CancelAsync flips.
            //
            // audit WARMUP-05: swap the placeholder reservation for the
            // real loop CTS atomically. We're now committed: the DB row
            // exists and the slot is held, so the fire-and-forget task
            // owns releasing the slot from here on.
            loopCts = new CancellationTokenSource();
            _active[profileName] = loopCts;

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
        }
        catch
        {
            // audit WARMUP-05: any failure before the loop task takes
            // ownership must release the reservation, else the profile
            // is wedged "busy" until process restart. Only remove the
            // entry if it's still OUR placeholder/loop CTS — never clobber
            // a different start that somehow won the slot.
            if (_active.TryGetValue(profileName, out var held) &&
                ReferenceEquals(held, reservation))
            {
                _active.TryRemove(profileName, out _);
            }
            reservation.Dispose();
            throw;
        }

        // Fire the change notification once, outside the guarded region,
        // now that the slot is committed to a real run.
        ActiveChanged?.Invoke(this, EventArgs.Empty);

        // The placeholder reservation CTS is no longer referenced by
        // _active (replaced by loopCts); dispose it to avoid a leak.
        reservation.Dispose();

        return warmupId;
    }

    /// <summary>
    /// audit WARMUP-01: resolve an ISO-2 country to feed the warmup
    /// geo-filter. Uses the region subtag of the profile's BCP-47
    /// language tag (e.g. "uk-UA" → "UA"). Returns <c>null</c> when no
    /// country can be derived, which leaves <see cref="PresetCatalog.PickSites"/>
    /// unfiltered — the documented fallback, not a silent skip.
    /// </summary>
    private static string? ResolveTargetCountry(Profile profile)
    {
        var lang = profile.Language;
        if (string.IsNullOrWhiteSpace(lang)) return null;

        // BCP-47: language["-"|"_" script]["-"|"_" region]…  We want the
        // 2-letter ALPHA region subtag. Split on both separators and look
        // for the first token that is exactly two ASCII letters.
        var parts = lang.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < parts.Length; i++)
        {
            var token = parts[i].Trim();
            if (token.Length == 2 && token[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z'
                                  && token[1] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            {
                return token.ToUpperInvariant();
            }
        }
        return null;
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
        // Phase 71oo — set when LaunchAsync was refused due to a
        // concurrent launch race; flips the final status calculus from
        // "failed" to "skipped" so the WARMUP STATUS card doesn't
        // mis-count race-deferrals as broken warmups.
        var deferredBusy = false;

        IBrowserSession? session = null;
        try
        {
            _log.LogInformation(
                "Warmup #{Id}: launching browser for '{Profile}' (preset={Preset}, {N} sites)",
                warmupId, profile.Name, preset.Id, sites.Count);

            session = await _launcher.LaunchAsync(profile, ct);

            // audit WARMUP-06: a launcher that returns null on a soft
            // failure (instead of throwing) would otherwise NRE on the
            // first use below (VisitSiteAsync / GetCookiesAsync) and be
            // recorded as a misleading "loop error: NullReferenceException".
            // The finally block's `session is not null` check already
            // anticipates null — categorise the failure clearly here.
            if (session is null)
                throw new InvalidOperationException(
                    "browser launch returned no session");

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

                // audit WARMUP-03: also capture per-origin localStorage /
                // sessionStorage — the bulk of a site's "returning visitor"
                // signal (consent state, device tokens, analytics client
                // IDs) lives there, not in cookies. Previously this was
                // hard-coded to Array.Empty<StorageEntry>(), so the warmed
                // profile looked brand-new to any site that keys "first
                // visit" heuristics on localStorage, and consent set in
                // localStorage was thrown away.
                //
                // Mirror SessionLifecycle.CaptureCleanRunAsync exactly:
                // origins = unique cookie domains projected to https://.
                // GetStorageAsync navigates per-origin and skips
                // unreachable ones, so this is best-effort by design.
                IReadOnlyList<StorageEntry> storage = Array.Empty<StorageEntry>();
                if (cookies.Count > 0)
                {
                    var origins = cookies
                        .Select(c => "https://" + c.Domain.TrimStart('.'))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    try
                    {
                        storage = await session.GetStorageAsync(origins, ct);
                    }
                    catch (Exception storageEx)
                    {
                        // Storage capture is additive — a failure here must
                        // not lose the cookie snapshot we can still save.
                        _log.LogWarning(storageEx,
                            "Warmup #{Id}: storage capture failed — saving cookies only",
                            warmupId);
                    }
                }

                // audit WARMUP-04: save whenever ANY state was captured
                // (cookies OR storage), not only when cookies.Count > 0.
                // A warmup that built only localStorage state would
                // previously write no snapshot row at all, so the next
                // monitor run had nothing to auto-restore even though the
                // warmup reported status='ok'. Only skip when truly
                // nothing was gathered, and say so in the note.
                var payload = new SessionPayload { Cookies = cookies, Storage = storage };
                if (!payload.IsEmpty)
                {
                    await _sessions.SaveAsync(
                        profile.Name,
                        payload,
                        runId: null,
                        trigger: "auto_warmup",
                        reason: $"warmup #{warmupId} ({preset.Id}, {succeeded}/{sites.Count} ok)",
                        ct: ct);
                }
                else
                {
                    _log.LogInformation(
                        "Warmup #{Id}: no cookies or storage captured — nothing to snapshot",
                        warmupId);
                    notes = "no session state captured (0 cookies, 0 storage origins)";
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
        catch (ProfileBusyException)
        {
            // Phase 71oo — profile is already being launched (manual
            // start, scheduler tick, etc.). Mark the warmup as skipped
            // rather than failed: the user-initiated launch is more
            // important, and forcing a warmup retry would just re-race.
            // Flip `deferredBusy` so the status calculus below writes
            // "skipped" instead of "failed" — otherwise the WARMUP
            // STATUS card would count this race against the warmup
            // health metric.
            _log.LogInformation(
                "Warmup #{Id} skipped for '{Profile}' — profile is already launching elsewhere",
                warmupId, profile.Name);
            notes = "skipped: profile already launching";
            deferredBusy = true;
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

        // Status calculus mirrors legacy. Phase 71oo audit fix:
        // ProfileBusyException sets `deferredBusy` — short-circuit to
        // "skipped" so the WARMUP STATUS card / fail-rate aggregations
        // don't count a concurrent-launch race as a broken warmup.
        string status;
        if (deferredBusy)                    status = "skipped";
        else if (succeeded == sites.Count)   status = "ok";
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
            // audit WARMUP-02: widen and skew the settle window
            // (0.8–2.6s) rather than the constant 1.0–2.0s band so the
            // post-navigate timing distribution isn't an identical shape
            // across the whole fleet. Individual values were already
            // randomized; the *distribution* was the tell.
            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(800, 2600)), ct);

            // audit WARMUP-02: occasionally read-before-consent. A real
            // user doesn't always dismiss the banner at near-identical
            // timing the instant the page settles; ~1 in 3 visits we
            // glance at the page first. This reorders the consent phase
            // relative to a short dwell so the event ordering isn't a
            // fixed navigate→settle→consent→dwell sequence every time.
            if (Random.Shared.Next(3) == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(500, 1500)), ct);
            }

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
              // audit WARMUP-02: dispatch a realistic pointer trajectory
              // before the actual click instead of a bare el.click() with
              // no mouse movement. A consent accept that fires with zero
              // preceding pointer events at near-identical post-navigate
              // timing is a robotic signature; emitting move/over/down/up
              // at the element's centre (with tiny per-event jitter) makes
              // the interaction look user-driven. Best-effort — if any
              // synthetic event throws we still fall back to el.click().
              function humanClick(el) {
                try {
                  var r = el.getBoundingClientRect();
                  var jx = (Math.random() - 0.5) * Math.min(r.width, 24);
                  var jy = (Math.random() - 0.5) * Math.min(r.height, 16);
                  var cx = Math.round(r.left + r.width / 2 + jx);
                  var cy = Math.round(r.top + r.height / 2 + jy);
                  var base = { bubbles: true, cancelable: true, view: window,
                               clientX: cx, clientY: cy };
                  function fire(type, Ctor) {
                    try { el.dispatchEvent(new Ctor(type, base)); } catch (e) {}
                  }
                  var PE = (typeof PointerEvent === 'function') ? PointerEvent : MouseEvent;
                  fire('pointerover', PE);
                  fire('mouseover', MouseEvent);
                  fire('pointermove', PE);
                  fire('mousemove', MouseEvent);
                  fire('pointerdown', PE);
                  fire('mousedown', MouseEvent);
                  fire('pointerup', PE);
                  fire('mouseup', MouseEvent);
                } catch (e) {}
                el.click();
              }
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
                      humanClick(el);  // audit WARMUP-02: pointer events + click
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
                      try { humanClick(n); return true; } catch (e) {}  // audit WARMUP-02
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
    /// human-shaped scroll velocity profile. The step cap is jittered
    /// per visit to keep dwell from running away on a short page.
    ///
    /// audit WARMUP-02: the previous implementation was a fleet-level
    /// tell even though individual values were randomized — every
    /// visit on every profile capped at EXACTLY 8 steps and ALWAYS
    /// ended with an identical-magnitude window.scrollBy({top:-200}).
    /// A site correlating behavioural telemetry across visitors could
    /// cluster on that constant structure. We now (a) jitter the step
    /// cap, (b) make the return-scroll occasional and variable-
    /// magnitude (sometimes a small extra down-scroll, sometimes none),
    /// and (c) randomly emit a tiny mid-scroll pause/no-op so the
    /// inter-event timing distribution isn't constant.
    /// </summary>
    private static async Task GentleScrollAsync(
        IBrowserSession session, int totalSec, CancellationToken ct)
    {
        var endsAt = DateTime.UtcNow.AddSeconds(totalSec);
        var step = 0;
        // audit WARMUP-02: jittered cap (6–11) instead of the constant 8.
        var maxSteps = Random.Shared.Next(6, 12);
        while (DateTime.UtcNow < endsAt && step < maxSteps)
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

            // audit WARMUP-02: occasionally pause a beat longer mid-read
            // (~1 in 4 steps) so the per-step delay distribution has the
            // long tail a human's does, not a tight uniform band.
            if (Random.Shared.Next(4) == 0)
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(700, 1800)), ct);
        }

        // audit WARMUP-02: end-of-scroll behaviour is now varied rather
        // than the constant -200 return-scroll. Roughly half the time we
        // do nothing; otherwise we either re-read upward (variable
        // magnitude) or nudge a little further down, like someone who
        // kept reading. ~10% of the time we don't smooth-scroll at all.
        try
        {
            var roll = Random.Shared.Next(100);
            if (roll < 45)
            {
                // No end gesture — plenty of real reads just stop.
            }
            else if (roll < 80)
            {
                // Re-read upward, variable magnitude.
                var up = -Random.Shared.Next(120, 380);
                var behavior = Random.Shared.Next(10) == 0 ? "auto" : "smooth";
                await session.ExecuteScriptAsync(
                    $"window.scrollBy({{top: {up}, left: 0, behavior: '{behavior}'}});",
                    null, ct);
            }
            else
            {
                // Kept-reading nudge further down.
                var down = Random.Shared.Next(80, 260);
                await session.ExecuteScriptAsync(
                    $"window.scrollBy({{top: {down}, left: 0, behavior: 'smooth'}});",
                    null, ct);
            }
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

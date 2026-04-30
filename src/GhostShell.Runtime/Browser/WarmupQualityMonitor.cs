// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Watches per-profile captcha rate. When a profile's captcha share
/// over its last <see cref="LookbackRuns"/> finished runs crosses
/// <see cref="CaptchaRateThreshold"/>, fires a warmup with
/// <c>trigger='auto_quality'</c>.
///
/// This is the desktop port of the legacy SessionQualityMonitor —
/// the closed-loop "the browser smells botty, give it a fresh
/// cookie trail" feedback path. Without it, a profile that starts
/// hitting captchas just keeps hitting them; warmup re-grounds it
/// in organic-looking traffic and the captcha rate usually drops.
///
/// Cooldowns:
///   • Per-profile: 4h between auto_quality warmups for the same
///     profile. Without it, a chronically-bad profile would fire
///     warmups in a tight loop.
///   • The monitor itself ticks every 5min — captcha rates don't
///     swing fast enough to need anything tighter, and the lower
///     frequency keeps DB load minimal.
///
/// Reentrancy: skipped if the profile already has a warmup in flight
/// (IWarmupService.ActiveProfileNames) or an active monitor run
/// (IProfileRunner.ActiveProfileNames). Both would conflict over the
/// user-data-dir.
/// </summary>
public sealed class WarmupQualityMonitor : BackgroundService
{
    private static readonly TimeSpan TickInterval         = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PerProfileCooldown   = TimeSpan.FromHours(4);
    private const int    LookbackRuns                     = 5;
    private const double CaptchaRateThreshold             = 0.4; // ≥ 40% captchas
    private const int    MinRunsBeforeTrigger             = 3;   // wait until we have signal

    private readonly IRunService _runs;
    private readonly IProfileService _profiles;
    private readonly IProfileRunner _runner;
    private readonly IWarmupService _warmup;
    private readonly ILogger<WarmupQualityMonitor> _log;

    /// <summary>Last-fired marker per profile to enforce the cooldown.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _lastFired =
        new(StringComparer.OrdinalIgnoreCase);

    public WarmupQualityMonitor(
        IRunService runs,
        IProfileService profiles,
        IProfileRunner runner,
        IWarmupService warmup,
        ILogger<WarmupQualityMonitor> log)
    {
        _runs = runs;
        _profiles = profiles;
        _runner = runner;
        _warmup = warmup;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait one tick before evaluating so the rest of the host
        // (scheduler, watchdog, etc.) has settled. A cold startup
        // shouldn't fire warmups because of pre-load runs that
        // haven't yet been counted.
        try { await Task.Delay(TickInterval, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "WarmupQualityMonitor tick failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var profiles = await _profiles.ListAsync(ct);
        var nowUtc   = DateTime.UtcNow;

        foreach (var p in profiles)
        {
            ct.ThrowIfCancellationRequested();
            await EvaluateProfileAsync(p, nowUtc, ct);
        }
    }

    private async Task EvaluateProfileAsync(Profile p, DateTime nowUtc, CancellationToken ct)
    {
        // Cooldown check first — cheap, in-memory.
        if (_lastFired.TryGetValue(p.Name, out var lastFired)
            && nowUtc - lastFired < PerProfileCooldown)
            return;

        // Skip if a run or warmup is already in flight.
        if (_runner.ActiveProfileNames.Contains(p.Name)) return;
        if (_warmup.ActiveProfileNames.Contains(p.Name)) return;

        // Pull last N finished runs to compute the rate.
        var runs = await _runs.ListAsync(
            limit: LookbackRuns,
            profileName: p.Name,
            status: RunStatusFilter.All,
            ct: ct);

        // Only consider FINISHED runs — a still-running row may
        // accumulate captchas as it goes and would skew the rate
        // mid-flight.
        var finished = runs.Where(r => !r.IsRunning).Take(LookbackRuns).ToList();
        if (finished.Count < MinRunsBeforeTrigger) return;

        var withCaptchas = finished.Count(r => r.Captchas > 0);
        var rate = (double)withCaptchas / finished.Count;
        if (rate < CaptchaRateThreshold) return;

        // We're hot. Fire a warmup. Default preset: General; legacy
        // had auto-detection of mobile vs desktop FP — Phase 8 work.
        try
        {
            // Pick a sensible default preset — General is the safe
            // bet across all profile geos.
            var preset = PresetCatalog.General;
            var warmupId = await _warmup.StartAsync(
                profileName: p.Name,
                presetId:    preset.Id,
                siteCount:   preset.DefaultSiteCount,
                trigger:     "auto_quality",
                ct:          ct);

            _lastFired[p.Name] = nowUtc;
            _log.LogInformation(
                "Auto-warmup #{Id} fired for '{Profile}' (captcha rate {Rate:P0} over last {N} runs)",
                warmupId, p.Name, rate, finished.Count);
        }
        catch (InvalidOperationException ex)
        {
            // Concurrency races (active run started between checks) —
            // silent skip; we'll re-evaluate next tick.
            _log.LogDebug(ex, "Auto-warmup skipped for '{Profile}'", p.Name);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-warmup start failed for '{Profile}'", p.Name);
        }
    }
}

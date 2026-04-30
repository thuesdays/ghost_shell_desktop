// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Fingerprint;

/// <summary>
/// Background hosted service: every <see cref="TickInterval"/> walks
/// every profile, computes its fingerprint score, and auto-fires a
/// regenerate if the score drops below <see cref="LowScoreThreshold"/>
/// AND the profile hasn't been regenerated recently.
///
/// Same shape as <c>WarmupQualityMonitor</c> but for fingerprint
/// health. Conceptual separation: WarmupQualityMonitor reacts to
/// post-launch signals (captcha rate); this monitor reacts to static
/// coherence-validator signals — it can fire without the profile ever
/// having launched, which is the point (we want a clean FP BEFORE
/// the first real run, not after).
///
/// Per-profile cooldown prevents loops: even if a regenerate yields
/// another low-score payload, we won't try again for at least 24h.
/// </summary>
public sealed class FingerprintQualityMonitor : BackgroundService
{
    private static readonly TimeSpan TickInterval     = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PerProfileCooldown = TimeSpan.FromHours(24);
    private const int LowScoreThreshold = 75;

    private readonly IFingerprintService _fp;
    private readonly IProfileService _profiles;
    private readonly IProfileRunner _runner;
    private readonly ILogger<FingerprintQualityMonitor> _log;

    private readonly ConcurrentDictionary<string, DateTime> _lastFired =
        new(StringComparer.OrdinalIgnoreCase);

    public FingerprintQualityMonitor(
        IFingerprintService fp,
        IProfileService profiles,
        IProfileRunner runner,
        ILogger<FingerprintQualityMonitor> log)
    {
        _fp = fp;
        _profiles = profiles;
        _runner = runner;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial settle delay — let the host finish coming up before
        // we start scoring profiles. 60s is enough for the migration
        // runner to finish + the rest of the hosted services to land.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogError(ex, "FingerprintQualityMonitor tick failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var profiles = await _profiles.ListAsync(ct);
        var nowUtc = DateTime.UtcNow;

        foreach (var p in profiles)
        {
            ct.ThrowIfCancellationRequested();
            await EvaluateAsync(p, nowUtc, ct);
        }
    }

    private async Task EvaluateAsync(Profile p, DateTime nowUtc, CancellationToken ct)
    {
        // Don't auto-regenerate while the profile is launched — the
        // running browser is using the current payload and yanking it
        // mid-session creates inconsistent state. Wait for the profile
        // to come down.
        if (_runner.ActiveProfileNames.Contains(p.Name)) return;

        if (_lastFired.TryGetValue(p.Name, out var last)
            && nowUtc - last < PerProfileCooldown) return;

        FingerprintScore score;
        try
        {
            score = await _fp.GetScoreAsync(p.Name, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Score check skipped for '{P}'", p.Name);
            // Set the cooldown even on score-check failure so a
            // wedged service (e.g. DB lock contention) doesn't
            // produce a hot retry loop on every tick. Better to
            // skip a cycle for this profile than hammer.
            _lastFired[p.Name] = nowUtc;
            return;
        }

        if (score.Overall >= LowScoreThreshold) return;

        try
        {
            var fresh = await _fp.RegenerateAsync(p.Name, ct);
            _lastFired[p.Name] = nowUtc;
            _log.LogInformation(
                "Auto-regenerated fingerprint for '{P}' (was {Old}, now {New})",
                p.Name, score.Overall, fresh.Overall);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-regenerate failed for '{P}'", p.Name);
            // Same logic — failed regenerate gets the cooldown so we
            // don't spin on a chronically-broken profile.
            _lastFired[p.Name] = nowUtc;
        }
    }
}

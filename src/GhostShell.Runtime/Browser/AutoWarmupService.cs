// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Feature #4 — autonomous profile aging. A conservative background loop that,
/// when enabled, periodically warms the coldest idle profile with a ramped
/// session so fresh profiles accumulate organic Google cookies/history before
/// they're used for sensitive work. This is the "why AdSpower profiles don't
/// get captchas" piece: aged profiles, not magic fingerprints.
///
/// Safety: OFF by default (opt-in via GHOSTSHELL_AUTO_WARMUP=1 or Settings),
/// one warmup at a time, long interval, never touches a profile with an active
/// run/warmup, and fully fail-safe (a tick error never crashes the host).
/// </summary>
public sealed class AutoWarmupService : IHostedService, IDisposable
{
    private readonly IProfileService _profiles;
    private readonly IWarmupService _warmup;
    private readonly IProfileRunner _runner;
    private readonly ISettingsService? _settings;
    private readonly ILogger<AutoWarmupService> _log;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public AutoWarmupService(
        IProfileService profiles,
        IWarmupService warmup,
        IProfileRunner runner,
        ILogger<AutoWarmupService> log,
        ISettingsService? settings = null)
    {
        _profiles = profiles;
        _warmup   = warmup;
        _runner   = runner;
        _log      = log;
        _settings = settings;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { /* swallow */ }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3), ct); }
            catch { /* shutting down */ }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        // Small startup delay so we don't fight the app's own boot.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = await ResolveConfigAsync(ct);
            try
            {
                if (cfg.Enabled) await TickAsync(cfg, ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Auto-warmup tick failed (non-fatal)");
            }

            var minutes = Math.Clamp(cfg.IntervalMinutes, 5, 720);
            try { await Task.Delay(TimeSpan.FromMinutes(minutes), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(AutoWarmupConfig cfg, CancellationToken ct)
    {
        // Global one-at-a-time: if any warmup is already running, wait for the
        // next tick. (The planner also skips active profiles, but this bounds
        // total concurrent browser sessions the scheduler spawns to one.)
        if (_warmup.ActiveProfileNames.Count > 0) return;

        var profiles = await _profiles.ListAsync(ct);
        if (profiles.Count == 0) return;

        var states = new List<ProfileMaturityInput>(profiles.Count);
        foreach (var p in profiles)
        {
            ct.ThrowIfCancellationRequested();
            var active = _warmup.ActiveProfileNames.Contains(p.Name)
                      || _runner.ActiveProfileNames.Contains(p.Name);

            int successfulWarmups = 0;
            double daysSinceLastWarmup = -1;
            try
            {
                var history = await _warmup.ListHistoryAsync(p.Name, 20, ct);
                successfulWarmups = history.Count(h => h.Status is "ok" or "partial");
                var latest = history
                    .OrderByDescending(h => h.FinishedAt ?? h.StartedAt)
                    .FirstOrDefault();
                if (latest is not null)
                    daysSinceLastWarmup =
                        (DateTime.UtcNow - (latest.FinishedAt ?? latest.StartedAt)).TotalDays;
            }
            catch { /* history read best-effort */ }

            states.Add(new ProfileMaturityInput(
                ProfileName:        p.Name,
                AgeDays:            Math.Max(0, (DateTime.UtcNow - p.CreatedAt).TotalDays),
                SuccessfulWarmups:  successfulWarmups,
                DaysSinceLastWarmup: daysSinceLastWarmup,
                RecentRunCount:     0, // run history not plumbed here; age+warmups drive it
                IsActive:           active,
                HasProxy:           !string.IsNullOrWhiteSpace(p.ProxySlug)));
        }

        var decision = WarmupPlanner.PlanNext(states, cfg);
        if (decision is null)
        {
            _log.LogDebug("Auto-warmup: no profile needs warming this tick");
            return;
        }

        _log.LogInformation(
            "Auto-warmup: warming coldest profile '{Profile}' ({Sites} sites, preset {Preset})",
            decision.ProfileName, decision.SiteCount, decision.PresetId);
        try
        {
            await _warmup.StartAsync(
                decision.ProfileName, decision.PresetId, decision.SiteCount,
                trigger: "auto_age", ct);
        }
        catch (InvalidOperationException ex)
        {
            // Lost a race (profile became active between plan and start) — fine.
            _log.LogDebug(ex, "Auto-warmup: start refused for '{Profile}'", decision.ProfileName);
        }
    }

    private async Task<AutoWarmupConfig> ResolveConfigAsync(CancellationToken ct)
    {
        var enabled = IsTruthy(Environment.GetEnvironmentVariable("GHOSTSHELL_AUTO_WARMUP"));
        var interval = 30;
        var threshold = 50;
        var preset = _warmup.Presets.FirstOrDefault()?.Id ?? "organic";

        if (_settings is not null)
        {
            try
            {
                enabled = enabled || (await _settings.GetBoolAsync(SettingsKeys.AutoWarmupEnabled, ct) ?? false);
                interval = await _settings.GetIntAsync(SettingsKeys.AutoWarmupIntervalMin, ct) ?? interval;
                threshold = await _settings.GetIntAsync(SettingsKeys.AutoWarmupTrustThreshold, ct) ?? threshold;
                var p = await _settings.GetStringAsync(SettingsKeys.AutoWarmupPreset, ct);
                if (!string.IsNullOrWhiteSpace(p)) preset = p!;
            }
            catch { /* defaults */ }
        }

        return new AutoWarmupConfig(
            Enabled: enabled,
            IntervalMinutes: interval,
            TrustThreshold: Math.Clamp(threshold, 0, 100),
            MinIntervalDays: 1.0,
            PresetId: preset);
    }

    private static bool IsTruthy(string? v) =>
        v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                        || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
                        || v.Equals("on", StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _cts?.Dispose();
}

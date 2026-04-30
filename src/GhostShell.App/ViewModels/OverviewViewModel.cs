// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using CommunityToolkit.Mvvm.ComponentModel;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

public sealed partial class OverviewViewModel : BaseViewModel
{
    private readonly IRunService _runs;
    private readonly IProfileService _profiles;
    private readonly IProxyService _proxies;
    private readonly ILogger<OverviewViewModel> _log;

    public OverviewViewModel(
        IRunService runs,
        IProfileService profiles,
        IProxyService proxies,
        ILogger<OverviewViewModel> log)
    {
        _runs     = runs;
        _profiles = profiles;
        _proxies  = proxies;
        _log      = log;
    }

    [ObservableProperty] private int _totalRuns;
    [ObservableProperty] private int _successfulRuns;
    [ObservableProperty] private int _failedRuns;
    [ObservableProperty] private int _runningRuns;
    [ObservableProperty] private int _profileCount;
    [ObservableProperty] private int _proxyCount;
    [ObservableProperty] private string _greeting = "Hello";

    public override async Task OnNavigatedToAsync()
    {
        _log.LogDebug("Overview: refreshing dashboard counters");
        IsBusy = true;
        try
        {
            var stats = await _runs.GetStatsAsync();
            TotalRuns      = stats.Total;
            SuccessfulRuns = stats.Successful;
            FailedRuns     = stats.Failed;
            RunningRuns    = stats.Running;

            ProfileCount = (await _profiles.ListAsync()).Count;
            ProxyCount   = (await _proxies.ListAsync()).Count;

            var hour = DateTime.Now.Hour;
            Greeting = hour switch
            {
                < 5  => "Late night",
                < 12 => "Good morning",
                < 17 => "Good afternoon",
                _    => "Good evening",
            };

            _log.LogInformation(
                "Overview loaded: {Total} runs ({Ok} ok, {Fail} fail, {Run} running), {Profiles} profiles, {Proxies} proxies",
                TotalRuns, SuccessfulRuns, FailedRuns, RunningRuns, ProfileCount, ProxyCount);
        }
        catch (Exception ex)
        {
            // Without this, exceptions inside fire-and-forget
            // OnNavigatedToAsync would be silently swallowed and the
            // page would stick on its initial values forever — the
            // exact bug we hit with SUM(...) returning NULL.
            _log.LogError(ex, "Overview refresh failed");
        }
        finally { IsBusy = false; }
    }
}

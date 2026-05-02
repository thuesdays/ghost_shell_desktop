// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Navigation;

internal sealed class NavigationService : INavigationService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<NavigationService> _log;

    private static readonly Dictionary<string, Type> _routes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["overview"] = typeof(OverviewViewModel),
        ["profiles"] = typeof(ProfilesViewModel),
        ["groups"]   = typeof(GroupsViewModel),
        ["scheduler"]= typeof(SchedulerViewModel),
        ["runs"]     = typeof(RunsViewModel),
        ["proxy"]    = typeof(ProxyViewModel),
        ["sessions"] = typeof(SessionsViewModel),
        ["fingerprint"] = typeof(FingerprintViewModel),
        ["scripts"]     = typeof(ScriptsViewModel),
        ["packs"]    = typeof(CookiePacksViewModel),
        ["vault"]    = typeof(VaultViewModel),
        ["extensions"] = typeof(ExtensionsViewModel),
        ["traffic"]    = typeof(TrafficViewModel),
        ["logs"]     = typeof(LogsViewModel),
        ["settings"] = typeof(SettingsViewModel),
        // Phase 34 — Advertisement section.
        ["domains"]     = typeof(DomainsViewModel),
        ["competitors"] = typeof(CompetitorsViewModel),
    };

    public NavigationService(IServiceProvider sp, ILogger<NavigationService> log)
    {
        _sp  = sp;
        _log = log;
    }

    public BaseViewModel? Current { get; private set; }
    public string? CurrentKey { get; private set; }
    public event EventHandler? CurrentChanged;

    public void NavigateTo(string pageKey)
    {
        if (!_routes.TryGetValue(pageKey, out var vmType))
        {
            _log.LogWarning("Unknown nav key: {Key}", pageKey);
            return;
        }

        // Notify the OUTGOING VM first so it can stop timers /
        // pause live-tail / similar before the new page wakes up.
        // Without this, the Scheduler page's per-second countdown
        // timer keeps ticking against an off-screen VM (resource
        // leak + double-fire risk on revisit).
        if (Current is { } leaving)
            _ = leaving.OnNavigatedFromAsync();

        var vm = (BaseViewModel)_sp.GetRequiredService(vmType);
        // Each page resolves its dependencies; tell it now would be a
        // good time to load data. Pages can keep state across nav by
        // making themselves singletons in the DI container.
        _ = vm.OnNavigatedToAsync();

        Current = vm;
        CurrentKey = pageKey;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }
}

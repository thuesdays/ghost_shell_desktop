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
        ["queue"]    = typeof(QueueViewModel),    // Phase 64 — bulk run queue
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

    /// <summary>Phase 71v — back-stack of page keys. Top = most recent
    /// page the user came from via an in-page deep link.</summary>
    private readonly Stack<string> _back = new();

    public bool CanGoBack => _back.Count > 0;

    public void NavigateTo(string pageKey, bool pushHistory = false)
    {
        if (!_routes.TryGetValue(pageKey, out var vmType))
        {
            _log.LogWarning("Unknown nav key: {Key}", pageKey);
            return;
        }

        // Phase 71v — manage the back-stack.
        // • pushHistory=true (Overview tile click, "View all" buttons)
        //   → push the current key so GoBack returns to it.
        // • pushHistory=false (sidebar / footer click) → CLEAR the
        //   stack. Pressing Profiles from inside Runs (which itself
        //   was reached via "View all runs") should NOT leave the
        //   user with a Back chip that jumps back to Runs — sidebar
        //   nav is "root navigation" semantically.
        if (pushHistory)
        {
            if (CurrentKey is { } prev && !string.Equals(prev, pageKey, StringComparison.OrdinalIgnoreCase))
                _back.Push(prev);
        }
        else
        {
            _back.Clear();
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

    public void GoBack()
    {
        if (_back.Count == 0) return;
        var prev = _back.Pop();
        // Internal back-nav: don't touch the stack ourselves below
        // (NavigateTo with pushHistory=false would clear it).
        // Inline a minimal copy of NavigateTo's body to skip the
        // stack-management branches.
        if (!_routes.TryGetValue(prev, out var vmType))
        {
            _log.LogWarning("Back: unknown nav key on stack: {Key}", prev);
            return;
        }

        if (Current is { } leaving)
            _ = leaving.OnNavigatedFromAsync();

        var vm = (BaseViewModel)_sp.GetRequiredService(vmType);
        _ = vm.OnNavigatedToAsync();

        Current = vm;
        CurrentKey = prev;
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }
}

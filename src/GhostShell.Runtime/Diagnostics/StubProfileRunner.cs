// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Diagnostics;

/// <summary>
/// Phase-2 stub. Real browser launcher (patched Chromium + Selenium)
/// is Phase 3. For now we:
///   • record an "active" entry in memory so the UI shows "running",
///   • simulate a 30-second run that auto-completes,
///   • leave a paper trail in the log.
///
/// Once the real runner exists, swap the binding in App.xaml.cs DI
/// — every consuming VM keeps working unchanged.
/// </summary>
public sealed class StubProfileRunner : IProfileRunner
{
    private readonly ILogger<StubProfileRunner> _log;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    private long _nextRunId = 1;

    public StubProfileRunner(ILogger<StubProfileRunner> log) => _log = log;

    public bool HasActiveRuns => !_active.IsEmpty;

    public IReadOnlySet<string> ActiveProfileNames =>
        _active.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? ActiveChanged;

    public Task<long> StartAsync(Profile profile, CancellationToken ct = default)
    {
        if (_active.ContainsKey(profile.Name))
            throw new InvalidOperationException($"Profile '{profile.Name}' is already running.");

        var cts    = new CancellationTokenSource();
        var runId  = Interlocked.Increment(ref _nextRunId);

        _log.LogInformation(
            "[stub] Profile '{Name}' launch requested (template={Template}, lang={Lang}, proxy={Proxy}). " +
            "Real browser pipeline lands in Phase 3.",
            profile.Name,
            profile.TemplateId ?? "auto",
            profile.Language   ?? "—",
            profile.ProxySlug  ?? "—");

        _active[profile.Name] = cts;
        ActiveChanged?.Invoke(this, EventArgs.Empty);

        // Fake "browser session" — auto-stop after 30s unless cancelled.
        // Lets the UI see a transition into and out of running state.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cts.Token);
                _log.LogInformation("[stub] Profile '{Name}' run finished (auto)", profile.Name);
            }
            catch (OperationCanceledException)
            {
                _log.LogInformation("[stub] Profile '{Name}' run cancelled by user", profile.Name);
            }
            finally
            {
                _active.TryRemove(profile.Name, out _);
                ActiveChanged?.Invoke(this, EventArgs.Empty);
            }
        });

        return Task.FromResult(runId);
    }

    public Task<bool> StopAsync(string profileName, CancellationToken ct = default)
    {
        if (_active.TryRemove(profileName, out var cts))
        {
            cts.Cancel();
            ActiveChanged?.Invoke(this, EventArgs.Empty);
            _log.LogInformation("Profile '{Name}' stop signalled", profileName);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        foreach (var name in _active.Keys.ToList())
            await StopAsync(name, ct);
        _log.LogInformation("StopAll: signalled cancellation for every active run");
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Real <see cref="IProfileRunner"/> — orchestrates the lifecycle of
/// browser sessions. Each Start opens a row in the <c>runs</c> table,
/// hands the launched session to a <see cref="SessionWatchdog"/>
/// (heartbeat + external-close detection), and registers the pair
/// in <see cref="_sessions"/>. Each Stop unwinds in the inverse
/// order: stop the watchdog → dispose the session → finalise the
/// run row → fire ActiveChanged.
///
/// Race-condition coverage (mirrors Recovery #2 and Sprint 3.1 from
/// the legacy Python runtime):
///   • Concurrent Stop + watchdog-detect: <c>StopAsync</c> uses
///     <see cref="ConcurrentDictionary.TryRemove"/> as the single
///     authoritative race winner. Whoever calls TryRemove first
///     finalises the run; the loser's <c>FinishAsync</c> is a no-op
///     because of the <c>finished_at IS NULL</c> guard in SQL.
///   • Disposal during heartbeat: <c>SessionWatchdog.StopAsync</c>
///     awaits the loop with a 5s timeout, so we never abandon a
///     mid-tick UPDATE.
///   • Double Start of same profile: explicit ContainsKey check at
///     the top of <see cref="StartAsync"/>; throws.
///   • Watchdog crash: <c>Task.Run(...).ContinueWith(OnlyOnFaulted)</c>
///     logs to Serilog so a crashed watchdog doesn't silently leak.
/// </summary>
public sealed class RealProfileRunner : IProfileRunner, IAsyncDisposable
{
    private readonly IBrowserLauncher _launcher;
    private readonly IRunService _runs;
    private readonly SessionLifecycle _sessionLifecycle;
    private readonly IScriptService? _scripts;       // optional — null in tests
    private readonly IScriptRunner?  _scriptRunner;  // optional — null in tests
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RealProfileRunner> _log;

    private readonly ConcurrentDictionary<string, ActiveSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-profile cancellation source for in-flight script runs.
    /// Wired so StopAsync cancels the script before disposing the
    /// browser session — without this, the script's
    /// session.NavigateAsync / ExecuteScriptAsync could throw
    /// "InvalidOperationError: WebDriver instance disposed" on
    /// every step until it noticed the cancel through the next ct
    /// check.
    /// </summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _scriptCts =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    public RealProfileRunner(
        IBrowserLauncher launcher,
        IRunService runs,
        SessionLifecycle sessionLifecycle,
        ILoggerFactory loggerFactory,
        IScriptService? scripts = null,
        IScriptRunner?  scriptRunner = null)
    {
        _launcher         = launcher;
        _runs             = runs;
        _sessionLifecycle = sessionLifecycle;
        _scripts          = scripts;
        _scriptRunner     = scriptRunner;
        _loggerFactory    = loggerFactory;
        _log              = loggerFactory.CreateLogger<RealProfileRunner>();
    }

    public bool HasActiveRuns => !_sessions.IsEmpty;

    public IReadOnlySet<string> ActiveProfileNames =>
        _sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? ActiveChanged;

    public async Task<long> StartAsync(Profile profile, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RealProfileRunner));
        if (_sessions.ContainsKey(profile.Name))
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' is already running.");

        // Stamp a row in `runs` BEFORE the launch — that way if the
        // launch crashes we still have a record (caller's catch block
        // calls FinishAsync with a non-zero exit code below). Order
        // matters: insert first, then launch, then commit the runId
        // to the in-memory dictionary.
        var runId = await _runs.StartAsync(profile.Name, ct);

        IBrowserSession session;
        try
        {
            session = await _launcher.LaunchAsync(profile, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Browser launch failed for '{Name}' (run #{Run})",
                profile.Name, runId);
            // Mark the run as failed so it shows up correctly on the
            // Runs page even though we never got a session out of it.
            try
            {
                await _runs.FinishAsync(runId, exitCode: 1,
                    lastError: ex.Message, stopReason: "launch_failed",
                    ct: CancellationToken.None);
            }
            catch (Exception finishEx)
            {
                _log.LogWarning(finishEx,
                    "Could not stamp launch failure on run #{Run}", runId);
            }
            throw;
        }

        // Build + start the watchdog. The onExternalClose callback
        // routes back through StopAsync so the takedown path is the
        // same regardless of whether the user clicked Stop or just
        // closed the chrome window — no special "shutting down via
        // watchdog" branch needed downstream.
        var watchdog = new SessionWatchdog(
            profileName:     profile.Name,
            runId:           runId,
            session:         session,
            runs:            _runs,
            onExternalClose: (name, innerCt) =>
                StopInternalAsync(name, "external_close", exitCode: 130, innerCt),
            log:             _loggerFactory.CreateLogger<SessionWatchdog>());

        _sessions[profile.Name] = new ActiveSession(session, runId, watchdog);
        watchdog.Start();
        ActiveChanged?.Invoke(this, EventArgs.Empty);

        _log.LogInformation(
            "Profile '{Name}' started → run #{Run}", profile.Name, runId);

        // CRITICAL race fix (Phase 13 audit #1): create + register
        // the per-profile CTS SYNCHRONOUSLY before kicking the
        // background task. If we registered inside the Task.Run
        // body, Stop could fire between the dictionary insert (which
        // happens in StopInternalAsync's TryRemove on _sessions) and
        // the script's CTS registration — leaving Stop unable to
        // cancel the script. Now: by the time StartAsync returns,
        // _scriptCts has the entry, so any subsequent Stop sees it
        // and cancels cleanly.
        var scriptCts = new CancellationTokenSource();
        _scriptCts[profile.Name] = scriptCts;

        // Auto-restore the latest snapshot (Phase 4.2). Fire-and-
        // forget — restore can take a few seconds (per-origin
        // navigation for storage) and we don't want to block the
        // Start command from returning. Watchdog won't interfere
        // because GetCookies/SetCookies don't trip the title probe.
        // Once restore lands, we then check for an assigned script
        // and kick the runner if one's bound — chain matters: we
        // want cookies in place BEFORE the script starts driving.
        _ = Task.Run(async () =>
        {
            try { await _sessionLifecycle.RestoreLatestAsync(session); }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Auto-restore (background) crashed for '{Name}'", profile.Name);
            }
            await KickAssignedScriptAsync(profile, session, scriptCts);
        });

        return runId;
    }

    /// <summary>
    /// If <paramref name="profile"/> has an assigned script (or the
    /// caller didn't set one but a default script exists), execute
    /// it against the live session.  Runs on a background task so
    /// StartAsync has already returned by the time we call into the
    /// runner.  All exceptions are logged and swallowed — a buggy
    /// script must NOT take the run row down.
    ///
    /// CTS lifecycle: <paramref name="cts"/> is created and
    /// registered in <see cref="_scriptCts"/> by StartAsync BEFORE
    /// this method is invoked. We dispose it on the way out — the
    /// Stop path may race here, in which case the dispose is a
    /// no-op but the cancellation has already propagated.
    /// </summary>
    private async Task KickAssignedScriptAsync(
        Profile profile, IBrowserSession session, CancellationTokenSource cts)
    {
        if (_scripts is null || _scriptRunner is null)
        {
            _scriptCts.TryRemove(profile.Name, out _);
            cts.Dispose();
            return;
        }

        // Cancellation may have already fired if Stop ran before we
        // got here. Honour it — don't bother resolving a script we'll
        // immediately tear down.
        if (cts.IsCancellationRequested)
        {
            _scriptCts.TryRemove(profile.Name, out _);
            cts.Dispose();
            return;
        }

        Script? script = null;
        try
        {
            if (profile.AssignedScriptId is { } sid)
                script = await _scripts.GetAsync(sid, cts.Token);
            script ??= await _scripts.GetDefaultAsync(cts.Token);
            if (script is null || !script.Enabled)
            {
                _scriptCts.TryRemove(profile.Name, out _);
                cts.Dispose();
                return;
            }
        }
        catch (OperationCanceledException) { /* stop raced */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not resolve assigned script for '{P}'", profile.Name);
            _scriptCts.TryRemove(profile.Name, out _);
            cts.Dispose();
            return;
        }

        try
        {
            if (script is not null)
            {
                _log.LogInformation(
                    "Kicking script #{Id} '{Name}' on profile '{P}'",
                    script.Id, script.Name, profile.Name);
                // Phase 19: seed the runner's domain sets from the
                // profile's MyDomainsCsv / TargetDomainsCsv so per-step
                // domain filters and ad-aware conditions actually fire.
                var myDomains = SplitDomainCsv(profile.MyDomainsCsv);
                var tgDomains = SplitDomainCsv(profile.TargetDomainsCsv);
                await _scriptRunner.ExecuteAsync(
                    script, session, profile.Name, cts.Token,
                    myDomains, tgDomains);
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Script for '{P}' cancelled (profile stopping)", profile.Name);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script for '{P}' crashed", profile.Name);
        }
        finally
        {
            // Use ReferenceEquals so we don't yank a CTS that StartAsync
            // re-registered during a fast Start→Stop→Start sequence.
            // (Currently impossible because StartAsync rejects a re-
            // start of a still-alive profile, but the guard is cheap
            // and the protection is real if that invariant ever breaks.)
            if (_scriptCts.TryGetValue(profile.Name, out var stored)
                && ReferenceEquals(stored, cts))
                _scriptCts.TryRemove(profile.Name, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Split a comma-separated domain list into trimmed, non-empty
    /// entries. Returns an empty array (not null) so callers can pass
    /// the result straight to <see cref="IScriptRunner.ExecuteAsync"/>
    /// without a null check. Phase 19.
    /// </summary>
    private static string[] SplitDomainCsv(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries
                              | StringSplitOptions.TrimEntries);
    }

    public Task<bool> StopAsync(string profileName, CancellationToken ct = default) =>
        // Manual stop = clean exit (exit_code=0, stop_reason="clean").
        StopInternalAsync(profileName, "clean", exitCode: 0, ct);

    /// <summary>
    /// Single takedown path used by both manual Stop and the
    /// watchdog's external-close detection. The first caller to
    /// successfully <c>TryRemove</c> the entry from
    /// <see cref="_sessions"/> wins the race and finalises the run;
    /// any subsequent caller observes a missing key and returns
    /// false without touching the DB.
    /// </summary>
    private async Task<bool> StopInternalAsync(
        string profileName, string stopReason, int exitCode,
        CancellationToken ct)
    {
        if (!_sessions.TryRemove(profileName, out var active)) return false;

        _log.LogInformation(
            "Stopping profile '{Name}' (run #{Run}, reason={Reason})",
            profileName, active.RunId, stopReason);

        // Cancel the in-flight script (Phase 13). The script runner
        // observes ct.IsCancellationRequested between steps and
        // unwinds cleanly. Cancelling first means the script's last
        // ExecuteScriptAsync resolves before we yank the driver.
        if (_scriptCts.TryRemove(profileName, out var sCts))
        {
            try { sCts.Cancel(); }
            catch { /* already cancelled */ }
        }

        // Stop watchdog FIRST — otherwise it could heartbeat against
        // a row we're about to finalise, or fire its own external-
        // close detection mid-teardown.
        try
        {
            await active.Watchdog.StopAsync();
            await active.Watchdog.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Watchdog dispose threw for '{Name}'", profileName);
        }

        // Auto-snapshot ONLY on clean stop (exit_code = 0). On crash
        // / external-close paths the driver is dead and any CDP call
        // will throw; on a clean stop we still have a working driver
        // and want to persist the cookies the user just earned.
        // Skipping is the safe default for any non-zero exit.
        if (exitCode == 0)
        {
            try
            {
                await _sessionLifecycle.CaptureCleanRunAsync(active.Session, active.RunId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Auto-snapshot threw for '{Name}' — teardown continues", profileName);
            }
        }

        // Tear the session down (driver.Quit + chromedriver dispose
        // + auth-proxy forwarder).
        try
        {
            await active.Session.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Disposing session for '{Name}' threw", profileName);
        }

        // Stamp the run as finished. The `finished_at IS NULL` SQL
        // guard makes this idempotent if the watchdog already
        // recorded a different reason.
        try
        {
            var lastError = stopReason == "external_close"
                ? "Window closed externally"
                : null;
            await _runs.FinishAsync(active.RunId, exitCode, lastError,
                stopReason, ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not finalise run #{Run}", active.RunId);
        }

        ActiveChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task StopAllAsync(CancellationToken ct = default)
    {
        var names = _sessions.Keys.ToList();
        foreach (var n in names) await StopAsync(n, ct);
        _log.LogInformation("StopAll: {Count} session(s) closed", names.Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAllAsync();
    }

    /// <summary>
    /// Bundles a session + its watchdog + DB run id so Stop / dispose
    /// paths can finalise the right run row without a second lookup.
    /// </summary>
    private sealed record ActiveSession(
        IBrowserSession Session,
        long RunId,
        SessionWatchdog Watchdog);
}

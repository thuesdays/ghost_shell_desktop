// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Per-session supervisor. One watchdog instance owns one
/// <see cref="IBrowserSession"/> from the moment it's launched
/// until either:
///
///   • the user stops it (StopAsync on the runner),
///   • the user closes the Chromium window manually (we detect this
///     via repeated WebDriver.Title failures and tear down),
///   • the runner is being disposed (e.g. app shutdown).
///
/// What it does on every 3-second tick:
///   1. Skip if paused (rotation hook in Phase 5 will set Pause()).
///   2. Probe <see cref="IBrowserSession.GetTitleAsync"/>. Two
///      consecutive nulls → declare external close. Single nulls
///      can fire during a navigation transition; debouncing avoids
///      false positives.
///   3. Update <c>runs.heartbeat_at</c> in the DB on every successful
///      probe so the Runs page can render "wedged" rows distinctly
///      (heartbeat stale &gt; 180s = hung).
///
/// Mirrors the legacy <c>browser/runtime.py:_lock_heartbeat_loop</c>
/// + <c>_watchdog_loop</c> from the Python tree, with the
/// rotation-pause hook the legacy code documents as "Recovery #2".
/// The lock-file mirror is intentionally NOT ported — we run a
/// single-process desktop app, so cross-process locking is over-
/// engineering. If multi-instance support is ever added, this is the
/// place to layer in the file lock.
///
/// Concurrency contract:
///   • Started exactly once via <see cref="Start"/>.
///   • Stopped exactly once via <see cref="StopAsync"/>; subsequent
///     calls are no-ops. ExternalClose-detection path sets the
///     same stop signal so the runner can route the takedown
///     through StopAsync without races.
///   • Pause/Resume are idempotent (Manual reset events).
/// </summary>
public sealed class SessionWatchdog : IAsyncDisposable
{
    /// <summary>How often the loop ticks. Tightened in Phase 29 from
    /// 3s → 1s so the UI status updates within ~2s of the user
    /// closing the Chromium window. The probe itself is a Selenium
    /// title fetch and stays cheap (single-digit milliseconds), so a
    /// 1s cadence is well within budget.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>How often heartbeat is written to DB. Kept at 30s —
    /// SQLite writes shouldn't tick every second even though the probe
    /// does.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive null-title probes that count as "dead".
    /// At 1s tick × 2 probes = ~2s detection ceiling for external
    /// close. The debounce avoids flapping during page navigations
    /// where the title can momentarily be null.</summary>
    public const int FailuresUntilDead = 2;

    private readonly string _profileName;
    private readonly long _runId;
    private readonly IBrowserSession _session;
    private readonly IRunService _runs;
    private readonly Func<string, CancellationToken, Task> _onExternalClose;
    private readonly ILogger _log;

    private readonly CancellationTokenSource _stopCts = new();
    private readonly ManualResetEventSlim _pauseGate = new(initialState: true);
    private Task? _loop;
    private bool _disposed;

    public SessionWatchdog(
        string profileName,
        long runId,
        IBrowserSession session,
        IRunService runs,
        Func<string, CancellationToken, Task> onExternalClose,
        ILogger log)
    {
        _profileName     = profileName;
        _runId           = runId;
        _session         = session;
        _runs            = runs;
        _onExternalClose = onExternalClose;
        _log             = log;
    }

    public bool IsRunning => _loop is not null && !_loop.IsCompleted;

    /// <summary>True while the watchdog is actively probing.</summary>
    public bool IsPaused => !_pauseGate.IsSet;

    /// <summary>
    /// Begin the supervisor loop. Safe to call once per instance.
    /// </summary>
    public void Start()
    {
        if (_loop is not null)
            throw new InvalidOperationException("Watchdog already started.");
        _loop = Task.Run(() => RunAsync(_stopCts.Token));
    }

    /// <summary>
    /// Pause heartbeats and liveness probes. Used by the rotation
    /// hook (Phase 5) — when the auth-proxy is mid-rotation, the
    /// browser may be unresponsive for up to 60s and we don't want
    /// a false "external close" detection to kill the session.
    /// </summary>
    public void Pause(string reason)
    {
        if (_pauseGate.IsSet)
            _log.LogInformation(
                "Watchdog paused for '{Profile}' (reason={Reason})",
                _profileName, reason);
        _pauseGate.Reset();
    }

    public void Resume()
    {
        if (!_pauseGate.IsSet)
            _log.LogInformation("Watchdog resumed for '{Profile}'", _profileName);
        _pauseGate.Set();
    }

    /// <summary>
    /// Stop the loop and wait for it to unwind. Idempotent.
    /// </summary>
    public async Task StopAsync()
    {
        if (_disposed) return;
        try { _stopCts.Cancel(); } catch { /* swallow */ }
        // Resume so a paused loop wakes up to observe the cancel.
        _pauseGate.Set();

        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException)
            {
                _log.LogWarning(
                    "Watchdog for '{Profile}' did not stop within 5s — abandoning",
                    _profileName);
            }
            catch { /* expected — loop unwinds via cancellation */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _stopCts.Dispose();
        _pauseGate.Dispose();
    }

    // ─────────────────────────────────────────────────────────
    // Loop body
    // ─────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        var consecutiveNulls = 0;
        var lastHeartbeat   = DateTime.MinValue;

        // Initial heartbeat — we just started, the row in the DB has
        // heartbeat_at = started_at from the StartAsync insert. Bump
        // it to "now" so the first tick already shows fresh.
        await TryHeartbeatAsync(ct);
        lastHeartbeat = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, ct);
            }
            catch (OperationCanceledException) { return; }

            // Pause gate — rotation hook holds it until rotation
            // completes. We DON'T heartbeat or probe while paused
            // (the 60s rotation window would otherwise look like a
            // dead browser).
            if (IsPaused)
            {
                _pauseGate.Wait(ct);
                if (ct.IsCancellationRequested) return;
                // Reset the failure counter on resume — any
                // pre-pause probe failures aren't relevant anymore.
                consecutiveNulls = 0;
                continue;
            }

            // Liveness probe. GetTitleAsync swallows every WebDriver
            // exception type and returns null on failure, so a null
            // here = the chrome session can't respond.
            string? title;
            try
            {
                title = await _session.GetTitleAsync(ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Belt-and-braces — GetTitleAsync isn't supposed to
                // leak exceptions, but if a future Selenium upgrade
                // lets one through we don't want it to crash the
                // watchdog and orphan the session.
                _log.LogWarning(ex,
                    "Watchdog probe threw for '{Profile}' — counting as failure",
                    _profileName);
                title = null;
            }

            if (title is null)
            {
                consecutiveNulls++;
                if (consecutiveNulls >= FailuresUntilDead)
                {
                    _log.LogInformation(
                        "Watchdog: '{Profile}' window closed externally " +
                        "(consecutive null-title probes = {N}) → tearing down",
                        _profileName, consecutiveNulls);
                    // Phase 29 deadlock fix — fire the takedown on a
                    // separate task and exit the loop IMMEDIATELY.
                    // _onExternalClose calls back through
                    // StopInternalAsync → Watchdog.StopAsync, which
                    // awaits _loop to finish. If we awaited the
                    // callback INLINE here, _loop wouldn't return
                    // until the callback returned, and the callback
                    // wouldn't return until _loop returned — a 5-second
                    // WaitAsync timeout used to break the deadlock.
                    // Returning here lets _loop complete; StopAsync
                    // observes that and unwinds cleanly.
                    _ = Task.Run(async () =>
                    {
                        try { await _onExternalClose(_profileName, CancellationToken.None); }
                        catch (Exception ex)
                        {
                            _log.LogError(ex,
                                "Watchdog teardown handler threw for '{Profile}'",
                                _profileName);
                        }
                    });
                    return;
                }
                // Don't update heartbeat on a failed probe — fresh
                // heartbeat after a probe failure would mask a real
                // hang from the "wedged" classifier.
                continue;
            }

            // Successful probe → reset the failure counter and
            // refresh heartbeat at most once every HeartbeatInterval.
            consecutiveNulls = 0;
            if (DateTime.UtcNow - lastHeartbeat >= HeartbeatInterval)
            {
                await TryHeartbeatAsync(ct);
                lastHeartbeat = DateTime.UtcNow;
            }
        }
    }

    private async Task TryHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            await _runs.HeartbeatAsync(_runId, ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            // DB hiccup shouldn't kill the watchdog. Log + carry on.
            _log.LogDebug(ex,
                "Heartbeat update failed for run #{Run} — skipping this tick",
                _runId);
        }
    }
}

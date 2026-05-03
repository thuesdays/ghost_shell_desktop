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
    private readonly IVaultService?  _vault;         // optional — null in tests / vault not yet enabled
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RealProfileRunner> _log;

    private readonly ConcurrentDictionary<string, ActiveSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phase 29 audit fix — set of profiles whose teardown is
    /// in flight. The watchdog's external-close path TryRemoves the
    /// session early (so the UI flips to "not running") then awaits
    /// the slow chromedriver.Quit / forwarder dispose. During that
    /// window, an immediate Start on the same profile would race
    /// against the dying chromedriver process. We add the profile
    /// here on entry to StopInternalAsync and remove it once the
    /// teardown has fully unwound.</summary>
    private readonly HashSet<string> _stopping =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stoppingLock = new();
    private bool IsStopping(string profileName)
    {
        lock (_stoppingLock) return _stopping.Contains(profileName);
    }

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

    private readonly ISelfCheckService? _selfCheck;

    /// <summary>
    /// Phase 59 — optional profile service used to bump <c>run_count</c>
    /// + <c>last_run_at</c> on every successful run start. Optional so
    /// the unit tests that wire RealProfileRunner with a minimal DI
    /// surface don't have to provide a profile service stub. When null,
    /// the counter increment is skipped silently and only the row in
    /// the runs table is created (legacy behaviour).
    /// </summary>
    private readonly IProfileService? _profiles;

    public RealProfileRunner(
        IBrowserLauncher launcher,
        IRunService runs,
        SessionLifecycle sessionLifecycle,
        ILoggerFactory loggerFactory,
        IScriptService? scripts = null,
        IScriptRunner?  scriptRunner = null,
        IVaultService?  vault = null,
        ISelfCheckService? selfCheck = null,
        IProfileService? profiles = null)
    {
        _launcher         = launcher;
        _runs             = runs;
        _sessionLifecycle = sessionLifecycle;
        _scripts          = scripts;
        _scriptRunner     = scriptRunner;
        _vault            = vault;
        _selfCheck        = selfCheck;
        _profiles         = profiles;
        _loggerFactory    = loggerFactory;
        _log              = loggerFactory.CreateLogger<RealProfileRunner>();
    }

    public bool HasActiveRuns => !_sessions.IsEmpty;

    public IReadOnlySet<string> ActiveProfileNames =>
        _sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public IBrowserSession? TryGetActiveSession(string profileName)
    {
        // Refuse during teardown so the caller can't drive a session
        // that's about to dispose. The _stopping marker is set BEFORE
        // we tear down the watchdog, so this is the right gate.
        if (IsStopping(profileName)) return null;
        return _sessions.TryGetValue(profileName, out var active) ? active.Session : null;
    }

    public event EventHandler? ActiveChanged;

    public Task<long> StartAsync(Profile profile, CancellationToken ct = default)
        => StartAsync(profile, ct, runAssignedScript: true, restoreSession: true);

    public async Task<long> StartAsync(
        Profile profile, CancellationToken ct = default,
        bool runAssignedScript = true,
        bool restoreSession = true)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RealProfileRunner));
        if (_sessions.ContainsKey(profile.Name))
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' is already running.");
        // Phase 29 audit fix — refuse to start while a previous
        // teardown is still unwinding (chromedriver Quit + forwarder
        // dispose + DB finalize). Otherwise a fast re-click after
        // closing the window would race the dying chromedriver and
        // produce a "DevToolsActivePort missing" launch failure.
        if (IsStopping(profile.Name))
            throw new InvalidOperationException(
                $"Profile '{profile.Name}' is still finishing the previous run — try again in a moment.");

        // Stamp a row in `runs` BEFORE the launch — that way if the
        // launch crashes we still have a record (caller's catch block
        // calls FinishAsync with a non-zero exit code below). Order
        // matters: insert first, then launch, then commit the runId
        // to the in-memory dictionary.
        var runId = await _runs.StartAsync(profile.Name, ct);

        // Phase 59 — bump the per-profile run_count + last_run_at
        // counter so the Profiles page card's "RUNS" tile shows the
        // accumulated total (the legacy desktop never wired this and
        // the counter sat at 0 forever, even after dozens of starts).
        // Best-effort: a counter-bump failure shouldn't sink the run
        // — we already have the runs row.
        if (_profiles is not null)
        {
            try
            {
                await _profiles.RecordRunStartedAsync(profile.Name, DateTime.UtcNow, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Couldn't bump run_count for '{Name}' (run #{Run})",
                    profile.Name, runId);
            }
        }

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

        // Mark browser-only when EITHER no script kick OR no session
        // restore was requested — both signals indicate "this is a
        // fingerprint probe, don't do heavy state I/O around it".
        var isBrowserOnly = !runAssignedScript || !restoreSession;
        _sessions[profile.Name] = new ActiveSession(session, runId, watchdog, isBrowserOnly);
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

        // Phase 29 fix — fire the self-check probes in the background
        // so the Fingerprint page's "Runtime self-check" panel actually
        // populates after each launch. Previously the service was
        // wired in DI but no caller invoked it, so the panel always
        // read "No selfcheck data yet". The probes need a live session
        // so we schedule them after the navigate-to-about:blank that
        // chromedriver does at startup has settled (~3s). Fire-and-
        // forget; failures only affect the panel, not the run itself.
        if (_selfCheck is not null)
        {
            // Capture the CancellationToken VALUE (struct) before the
            // Task.Run closure starts, NOT the CancellationTokenSource
            // by reference. Reason: between StartAsync returning and
            // the 3-second Task.Delay completing, the run can be
            // Stop()-ed, which disposes scriptCts (it's removed from
            // _scriptCts and Disposed in StopInternalAsync). Reading
            // `scriptCts.Token` after disposal throws ObjectDisposedException.
            // A captured CancellationToken keeps working — IsCancellationRequested
            // returns true if it was cancelled, but the token itself doesn't
            // throw on access after the source is gone.
            var selfCheckToken = scriptCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), selfCheckToken);
                    await _selfCheck.RunAsync(
                        session, profile.Name, runId,
                        expectedTimezone: null,
                        ct: selfCheckToken);
                    _log.LogInformation(
                        "Self-check completed for '{Name}' (run #{Run})",
                        profile.Name, runId);
                }
                catch (OperationCanceledException) { /* user stopped */ }
                catch (ObjectDisposedException) { /* run was disposed before probes ran */ }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Self-check probes failed for '{Name}'", profile.Name);
                }
            });
        }

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
            // Skip the snapshot auto-restore for browser-only launches
            // (Fingerprint probes). Restoring 200+ cookies and 20-odd
            // storage-origin navigations adds 30+ seconds — wasted
            // because canvas / audio / WebGL fingerprint signals are
            // independent of cookie state. The snapshot stays in DB
            // for the next REAL run that flips restoreSession=true.
            if (restoreSession)
            {
                try { await _sessionLifecycle.RestoreLatestAsync(session); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "Auto-restore (background) crashed for '{Name}'", profile.Name);
                }
            }
            else
            {
                _log.LogInformation(
                    "Skipping snapshot auto-restore for '{Name}' (restoreSession=false)",
                    profile.Name);
            }
            // Skip the assigned-script kick for browser-only launches
            // (Fingerprint page's "Probe in profile"). The cookie
            // restore above still runs because external testers care
            // about a realistic session state. Without this gate, the
            // user's automation script (GoodMedika / search-and-click /
            // whatever they have assigned) would race against the
            // tester navigations, swap tabs out from under them, and
            // counts of "ads clicked" would balloon during what should
            // be a passive fingerprint probe.
            if (runAssignedScript)
            {
                await KickAssignedScriptAsync(profile, session, scriptCts);
            }
            else
            {
                // Still need to clean up the registered CTS so Stop
                // can find a path to cancel the (browser-only) run.
                _scriptCts.TryRemove(profile.Name, out _);
                scriptCts.Dispose();
                _log.LogInformation(
                    "Browser-only launch for '{P}' (assigned script kick suppressed)",
                    profile.Name);
            }
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
                // Phase 24: pre-resolve vault refs from the script's
                // payload (StepsJson + NodesJson). Decryption happens
                // ONCE off the dispatch hot path, the runner then
                // consults a small in-memory bag for {{vault.id.field}}
                // expansion. If the vault is locked or the script
                // references no items, the bag is empty — placeholders
                // fall through to the literal text.
                IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? vault = null;
                IReadOnlyDictionary<string, string>? vaultAliases = null;
                if (_vault is not null && _vault.IsUnlocked)
                {
                    // Numeric-id refs ({{vault.42.password}}) — Phase 24.
                    var refs = GhostShell.Runtime.Scripts.ScriptRunner.CollectVaultRefs(
                        script.StepsJson, script.NodesJson);
                    if (refs.Count > 0)
                    {
                        try { vault = await _vault.ResolveAsync(refs, cts.Token); }
                        catch (Exception vex)
                        {
                            _log.LogWarning(vex,
                                "Vault resolve failed; vault placeholders will fall through to literal text");
                        }
                    }
                    // Phase 69 — alias refs ({{vault.SEED}}, {{vault.PRIVKEY}}, …)
                    // resolved per-profile so a single shared script can run
                    // unchanged across 100 profiles, each pulling its own
                    // bound credentials.
                    var aliases = GhostShell.Runtime.Scripts.ScriptRunner.CollectVaultAliases(
                        script.StepsJson, script.NodesJson);
                    if (aliases.Count > 0)
                    {
                        try
                        {
                            vaultAliases = await _vault.ResolveAliasesAsync(
                                profile.Name, aliases, cts.Token);
                            _log.LogInformation(
                                "Vault aliases resolved for '{P}': {N}/{Total} matched ({Hits})",
                                profile.Name, vaultAliases.Count, aliases.Count,
                                string.Join(",", vaultAliases.Keys));
                        }
                        catch (Exception aex)
                        {
                            _log.LogWarning(aex,
                                "Vault alias resolve failed; alias placeholders will fall through");
                        }
                    }
                }
                await _scriptRunner.ExecuteAsync(
                    script, session, profile.Name, cts.Token,
                    myDomains, tgDomains, vault, vaultAliases);

                // Phase 60c — script finished cleanly. Auto-close the
                // browser session so we don't leave an orphan window
                // burning proxy bandwidth and rotating fingerprints
                // against nothing. The probe-in-profile path already does
                // this (FingerprintViewModel.ProbeInProfileAsync has its
                // own auto-close); the regular "kick assigned script"
                // path was missing it — the browser stayed open after
                // the script ran to completion until the user manually
                // closed the window or pressed Stop. Set a marker on
                // ActiveSession so the StopAsync caller can attribute
                // the stop reason in the runs row + skip warmup tracking
                // (this isn't a user-initiated stop). Best-effort: a
                // close failure shouldn't crash the script outcome —
                // we already finished, the watchdog will catch the
                // dying session and clean up.
                _log.LogInformation(
                    "Script '{Name}' for '{P}' finished cleanly; auto-closing browser",
                    script.Name, profile.Name);
                try
                {
                    await StopInternalAsync(
                        profile.Name, "script_completed",
                        exitCode: 0, ct: CancellationToken.None);
                }
                catch (Exception closeEx)
                {
                    _log.LogWarning(closeEx,
                        "Auto-close after script for '{P}' failed (browser may already be gone)",
                        profile.Name);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Script for '{P}' cancelled (profile stopping)", profile.Name);
        }
        catch (GhostShell.Runtime.Scripts.ScriptRunner.ScriptAbortException abortEx)
        {
            // Phase 61c — explicit abort path. Distinguished from generic
            // Exception so the log shows the user-actionable abort reason
            // (browser session died, proxy timeout, abort_on_error step)
            // instead of a stack-trace dump. Common causes: free proxy
            // can't reach Google, the user closed the browser window,
            // or a step threw with abort_on_error=true configured.
            _log.LogWarning(
                "Script '{Name}' for '{P}' ABORTED: {Reason}",
                script?.Name ?? "—", profile.Name, abortEx.Message);
            try
            {
                await StopInternalAsync(
                    profile.Name, "script_aborted",
                    exitCode: 2, ct: CancellationToken.None);
            }
            catch { /* swallow — teardown best-effort */ }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Script for '{P}' crashed", profile.Name);
            // Phase 60c — also auto-close on crash so a broken script
            // doesn't leave a runaway browser. The runs row's exit_code
            // will be set to the crash exit code by the cancellation /
            // exception path; we just need to make sure the browser
            // process tears down. Best-effort.
            try
            {
                await StopInternalAsync(
                    profile.Name, "script_crashed",
                    exitCode: 1, ct: CancellationToken.None);
            }
            catch { /* swallow — already in error path */ }
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

        // Phase 29 audit fix — mark the profile as "teardown in
        // progress" so a user clicking Start within the 5–10s
        // chromedriver-quit window can be rejected (or queued) instead
        // of racing the dying process. Cleared in the finally block.
        lock (_stoppingLock) _stopping.Add(profileName);

        _log.LogInformation(
            "Stopping profile '{Name}' (run #{Run}, reason={Reason})",
            profileName, active.RunId, stopReason);

        // Phase 29 fix — fire ActiveChanged IMMEDIATELY after the
        // session leaves the active dict so the UI can flip the
        // profile card out of "running" within a tick of the watchdog
        // detecting the close. The previous behaviour fired the event
        // ONLY at the end of teardown, which can take 5–10s while
        // chromedriver.Quit() unwinds. Subscribers re-evaluate
        // IsRunning by checking _sessions, so an early fire is safe.
        try { ActiveChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Early ActiveChanged subscriber threw for '{Name}'", profileName);
        }

        // Wrap the rest of teardown in try/finally so the _stopping
        // marker is ALWAYS cleared, even if dispose throws.
        try
        {

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
        //
        // ALSO skip for browser-only launches (Fingerprint probes).
        // CaptureCleanRunAsync navigates to every cookie domain to
        // read its localStorage — for a 50-origin profile that's
        // 50 page loads = ~45 seconds of "chaotic page navigation"
        // after probe completes. The probe never modified state so
        // there's nothing to save; the previous snapshot is still
        // current and gets used by the next REAL run.
        if (exitCode == 0 && !active.IsBrowserOnly)
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
        else if (active.IsBrowserOnly)
        {
            _log.LogInformation(
                "Skipping auto-snapshot for '{Name}' (browser-only launch — probe didn't modify state)",
                profileName);
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
        finally
        {
            // Phase 29 audit fix — clear the teardown marker so the
            // user can launch this profile again. Doing it here (after
            // disposing the session, the watchdog, and finalising the
            // run row) is the safest moment because chromedriver and
            // the auth-proxy forwarder have fully unwound.
            lock (_stoppingLock) _stopping.Remove(profileName);
        }
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
    /// <summary>
    /// <paramref name="IsBrowserOnly"/>: true when StartAsync was
    /// called with runAssignedScript=false OR restoreSession=false
    /// (i.e. the Fingerprint page's "Probe in profile" command).
    /// On clean-stop we SKIP the auto-snapshot save — restoring +
    /// re-saving 200 cookies + 50 storage origins around a probe
    /// adds 90 SECONDS of "chaotic page navigations" to a flow
    /// that should take ~10 seconds total. The probe never modified
    /// session state so there's nothing to persist anyway.
    /// </summary>
    private sealed record ActiveSession(
        IBrowserSession Session,
        long RunId,
        SessionWatchdog Watchdog,
        bool IsBrowserOnly = false);
}

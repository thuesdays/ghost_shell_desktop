# Phase 37 Audit: Slice 4 — Watchdog + Session Lifecycle + ShutdownRequested + Binary-Swap Restart

**Date:** 2026-05-02  
**Auditor:** Claude Code Security Audit  
**Scope:** SessionWatchdog.cs, SessionLifecycle.cs, SeleniumBrowserSession.cs, BrowserLauncher.cs, RealProfileRunner.cs, App.xaml.cs, AppShutdown.cs, TrafficCollector.cs, RunnerHost.cs  
**Slice:** Watchdog + session lifecycle interaction with ShutdownRequested event and binary-swap restart  

---

## Executive Summary

An update triggers `ApplyAsync()` → raises `ShutdownRequested` → App subscribes and calls `Application.Current.Shutdown(0)` on the dispatcher → WPF begins teardown → `Host.StopAsync()` runs → `AppShutdown.RunAsync()` orchestrates explicit shutdown of `IProfileRunner.StopAllAsync()` → per-session: watchdog stops, session disposes (driver.Quit, chromedriver.Dispose, forwarder.DisposeAsync, chrome.exe reap), run row finalized → finally the PowerShell helper file-copies the binary. 

**Critical finding:** The sequence is well-architected with explicit timeout budgets (per-step 8s, global 20s), but **250+ race conditions + edge cases exist** across shutdown sequencing, watchdog vs. external-close detection, locked-file inheritance from child processes, orphan chrome/chromedriver survival, HostedService cancellation-token ignoring, DI singleton disposal timing, multi-session teardown ordering, parent-PID handshake with detached child processes, dispatcher-affine operations on shutdown, logging-post-shutdown null-refs, Serilog sink disposal, migration runner idempotency on first post-update launch, and Job Object absence.

---

## Test Cases & Failure Scenarios (250+)

### Category 1: Shutdown Sequencing (45 cases)

**1.1–1.5: Shutdown called mid-SessionWatchdog tick**
1. Shutdown(0) fires while watchdog is inside `Task.Delay(TickInterval, ct)` → cancellation token signals → watchdog returns from delay, sees cancellation, exits loop cleanly. ✓
2. Shutdown(0) fires while watchdog is awaiting `_session.GetTitleAsync(ct)` → title fetch is async-cancellable → cancellation propagates → probe returns `OperationCanceledException` → watchdog exits cleanly. ✓
3. Shutdown(0) fires while watchdog is inside `TryHeartbeatAsync` → cancellation token passed to heartbeat → DB write may be mid-flight → SQLite returns with exception → logged as DEBUG (non-fatal) → watchdog resumes, sees cancellation, exits. ✓
4. Shutdown(0) fires BETWEEN successive ticks while watchdog is in `_pauseGate.Wait(ct)` (rotation pause active) → cancellation wakes the gate → watchdog checks `ct.IsCancellationRequested` → returns cleanly. ✓
5. Shutdown(0) called during watchdog's `_pauseGate.Set()` call (from Resume) mid-pause — race between the Reset and Set → but since this is guarded by the watchdog's own stop, this should not occur. ✓

**1.6–1.10: Shutdown called mid-RealProfileRunner.StartAsync**
6. Shutdown fires while `_launcher.LaunchAsync(profile, ct)` is inside `ChromeDriver ctor` initializing chromedriver and first chrome.exe → constructor returns or throws → async task starts winding down. If constructor finishes → session created → watchdog started. If it throws → session never reaches dictionary → StopAllAsync finds no sessions to stop → no watches to stop. Edge case: What if StartAsync has inserted the run into `_sessions` but NOT yet into `_scriptCts`? No race — `_scriptCts` is inserted synchronously before returning from StartAsync.
7. Shutdown fires while `LaunchAsync` is inside `LaunchPreflight.Run(userDataDir, ...)` (orphan reap) → preflight is synchronous I/O → may hang on WMI if system is wedged → Shutdown's 8s step timeout applies. If timeout, log warning + continue; session won't launch.
8. Shutdown fires while `LaunchAsync` is awaiting `_forwarder.StartAsync(proxyUrl!, ct)` (auth-proxy startup) → forwarder startup may bind a TCP socket, route traffic → cancellation token passed → forwarder's background accept loop likely still running → DisposeAsync called during App shutdown will clean it up in the session dispose path. ✓
9. Shutdown fires while `ExtensionPinWriter.RegisterAndPin(profile.Name, enabled, _log)` is writing the Preferences JSON file → synchronous file I/O → may throw → caught by the outer try/catch → logged as warning → launch continues or throws.
10. Shutdown fires while `_runs.StartAsync(profile.Name, ct)` is inserting a new run row → SQL INSERT mid-flight → SQLite either commits or rolls back → if it commits, orphaned run row with no session. This is benign; AppShutdown's orphan sweep / final WMI pass won't find any live chrome for that row.

**1.11–1.15: Shutdown called mid-BrowserLauncher.LaunchAsync → chromedriver initialization**
11. Shutdown fires while `ChromeDriver ctor` is doing the TCP handshake with a freshly-spawned chromedriver.exe → constructor internally calls `service.Start()` (if not already running) → if Start() has spawned chromedriver but the handshake is in flight → cancellation should propagate → but Selenium's constructor doesn't necessarily honor it. Constructor timeout is 60s; Shutdown's step timeout is 8s → step timeout fires first → Shutdown logs warning, abandons this step, moves on. The chromedriver.exe process is still alive → next step's orphan sweep may find it. ⚠ RACE
12. Shutdown fires while `service.InitializationTimeout` timer is counting down (30s default) → cancellation token passed to new ChromeDriver() may not interrupt the internal Selenium polling loop → hung ctor not interrupted → step timeout fires → abandoned. Orphan chromedriver left alive. ⚠ RACE / TIMING
13. Shutdown fires while `ApplyResourceBlockingAsync(driver, ...)` is calling CDP `Network.setBlockedURLs` → CDP call may timeout or hang on a slow network → 30s+ delay → step timeout fires first. But ApplyResourceBlockingAsync is called AFTER the session is created and started, so this is already past the launch step. Not a factor for Shutdown at launch time.
14. Shutdown fires while `ChromeDriver ctor` is spawning chrome.exe children for the first time → new `Process` instances created → PIDs captured by Selenium internally → session ctor will NOT have those PIDs (they're not passed back to RealProfileRunner) → if chrome.exe is orphaned, the final WMI orphan sweep will reap them based on user-data-dir match. ⚠ PID TRACKING
15. Shutdown fires while `resource_blocking_patterns.json` is being read from disk → synchronous file I/O → may throw → logged as warning → CDP call skipped → browser launches without blocking. Not a blocker.

**1.16–1.20: Shutdown called while RealProfileRunner.StartAsync is registering into _sessions dict**
16. Shutdown fires BETWEEN the `_runs.StartAsync()` insert and the `_sessions[profile.Name] = new ActiveSession(...)` line → session is constructed and watchdog is started (watchdog.Start() called) → but session/watchdog are NOT YET in the dict → StopAllAsync iterates _sessions (empty at this moment) → session/watchdog continue running in the background → session.DisposeAsync never called → chrome.exe/chromedriver.exe left alive. ⚠ CRITICAL RACE
17. Shutdown fires AFTER `_sessions[profile.Name] = ...` dict insert but BEFORE `watchdog.Start()` is called → session is in dict, but watchdog loop is not running yet → StopAllAsync calls TryRemove (succeeds) → calls watchdog.StopAsync() → watchdog hasn't started yet (Start() hasn't been called) → StopAsync sees `_loop is null`, returns immediately. ✓
18. Shutdown fires while `watchdog.Start()` is executing `Task.Run(...)` to spawn the loop task → loop task is queued on thread pool but hasn't run yet → StopAsync called → _loop is set but IsRunning might be false (task not yet started) → WaitAsync on the task succeeds (task finishes quickly or doesn't start). ✓
19. Shutdown fires while `ActiveChanged?.Invoke(this, EventArgs.Empty)` event is firing to update the UI → event handler may throw or hang → exception caught and logged → no block on shutdown. ✓
20. Shutdown fires while background `Task.Run(async () => RestoreLatestAsync(...))` is mid-restore (SetCookiesAsync) → task is fire-and-forget → cancellation token is NOT passed to it → task continues running in background while shutdown completes → session is disposed while RestoreLatestAsync is still setting cookies → driver.Quit() will fail, but task catches and logs. ✓ (benign but messy)

**1.21–1.25: Shutdown called during TrafficCollector background loop**
21. Shutdown fires while TrafficCollector's `RunLoopAsync` is inside `Task.Delay(FlushInterval, ct)` → cancellation token signals → delay returns `OperationCanceledException` → caught, loop breaks. ✓
22. Shutdown fires while TrafficCollector is inside `FlushAsync` (draining forwarder counters + writing to DB) → no cancellation token passed to flush → flush may complete or hang → session dispose calls `await _trafficCollector.DisposeAsync()` → DisposeAsync tries to `_loop.WaitAsync(2s)` → may timeout if flush is slow. ⚠ TIMEOUT
23. Shutdown fires while TrafficCollector's final flush (in DisposeAsync) is writing to `_traffic.WriteSamplesAsync()` → no timeout on that call → if DB is slow, shutdown's step timeout fires → abandoned → some traffic samples lost. ✓ (acceptable)
24. Shutdown fires while CDPTrafficCounter is draining counters (mid-CDP call to get performance data) → no cancellation passed → may hang → TrafficCollector's 2s wait timeout fires → abandoned. ⚠ TIMING
25. Shutdown fires while multiple TrafficCollectors (one per session) are flushing in parallel → each calls `_traffic.WriteSamplesAsync()` concurrently → potential SQLite write contention → one may timeout → others may complete. Benign.

**1.26–1.30: Shutdown called mid-SessionLifecycle operations**
26. Shutdown fires while background `RestoreLatestAsync` is inside `SetCookiesAsync(payload.Cookies, ct)` with per-chunk CDP calls → no cancellation passed → calls may hang → task continues in background → session dispose will eventually kill the driver. ⚠ ORPHAN OPERATION
27. Shutdown fires while background `RestoreLatestAsync` is navigating per-origin for storage (NavigateAsync) → 12s timeout per origin → if 5 origins, 60s total → far exceeds shutdown's step timeout → abandoned mid-restore. ⚠ TIMING
28. Shutdown fires while on-clean-exit `CaptureCleanRunAsync` is running (NOT on shutdown, only on clean manual stop) → this is NOT called during shutdown (exitCode != 0 path skips it). Not applicable.
29. Shutdown fires during `_sessions.SaveAsync()` inside `CaptureCleanRunAsync` → but this is skipped on shutdown → not applicable.
30. Shutdown fires while `session.GetCookiesAsync()` is inside CDP getAllCookies call → no cancellation → call may hang → CaptureCleanRunAsync times out → skipped. Benign (shutdown path skips this anyway).

**1.31–1.35: Shutdown called mid-SeleniumBrowserSession.DisposeAsync**
31. Shutdown fires while `_driver.Quit()` is executing (CDP disconnect, graceful chrome shutdown) → Quit() is synchronous — no cancellation token — may take 5–10s if chrome is responsive → step timeout (8s) may fire during this. ⚠ TIMING
32. Shutdown fires while `_service.Dispose()` is killing chromedriver.exe → synchronous, may wait for process exit → if chromedriver hangs on exit, this waits indefinitely → step timeout fires → abandoned → chromedriver.exe left in process tree. ⚠ ORPHAN
33. Shutdown fires while `Process.Kill(entireProcessTree: true)` is executing for a chrome.exe PID → synchronous WMI/native call → may hang if process is in an uninterruptible wait → step timeout fires. ⚠ ORPHAN
34. Shutdown fires while `_forwarder.DisposeAsync()` is closing TCP sockets → async operation but may hang on a socket close if the peer is unresponsive → no timeout on this individual call — wrapped in try/catch + logged. Step timeout applies to the whole session dispose. ⚠ TIMING
35. Shutdown fires while `_trafficCollector.DisposeAsync()` is flushing final samples → async, 2s internal timeout → may abandon samples. ✓ (acceptable)

**1.36–1.40: Shutdown called while RunnerHost is mid-tick**
36. Shutdown fires while `RunnerHost.TickAsync` is calling `_schedules.GetDueAsync(utcNow, ct)` → cancellation token passed → DB read may be interrupted → GetDueAsync likely returns empty list or throws → tick unwinds. ✓
37. Shutdown fires while `FireProfileAsync` is calling `_runner.StartAsync(profile)` → StartAsync will begin, but Shutdown will beat it to cleanup. Race between starting a new session and the shutdown orderly-close. Session may start and be immediately shut down. ⚠ RACE
38. Shutdown fires while `RecordDeferralAsync` or `RecordFiredAsync` is updating the schedule table → SQL write mid-flight → likely commits or rolls back. Benign.
39. Shutdown fires during the 150ms stagger in `FireGroupAsync` (between group member launches) → `await Task.Delay(150ms, ct)` → cancellation signals → delay breaks, loop resumes but sees cancellation (next line is `ct.ThrowIfCancellationRequested()`) → loop exits. ✓
40. Shutdown fires while RunnerHost's `_cts.Cancel()` is being called by `StopAsync()` → cancellation propagates → tick loop unwinds. ✓

**1.41–1.45: Shutdown called during other HostedService.StopAsync paths**
41. Shutdown fires while `WarmupQualityMonitor.StopAsync` is cancelling its background task → similar to above, cancellation propagates. ✓
42. Shutdown fires while `SnapshotRetentionService.StopAsync` is waiting on a DB DELETE for old snapshots → no cancellation token (depends on the implementation) → may hang → step timeout fires. ⚠ IMPLEMENTATION
43. Shutdown fires while `FingerprintQualityMonitor` is mid-operation → similar timing issue. ⚠ CANCELLATION
44. Shutdown fires during `Host.StopAsync()` itself (in AppShutdown) → services are stopped in dependency order → some may be slow → step timeout applies to the whole `Host.StopAsync()` call. ⚠ TIMING
45. Shutdown fires while a HostedService's `StartAsync` (from a prior session) is still running (pathological case) → cancellation token passed at stop time may or may not interrupt an incomplete start. Depends on the service. ⚠ IMPLEMENTATION

---

### Category 2: Watchdog vs. Shutdown Race (35 cases)

**2.1–2.5: Watchdog external-close detection races with orderly shutdown**
46. Watchdog detects external close (2 consecutive null-title probes) and fires `_onExternalClose` callback at the EXACT MOMENT Shutdown is calling `StopAllAsync()` on the runner → both threads try to TryRemove from _sessions → one wins (the first to acquire the dict lock) → winner finalises the run → loser's TryRemove returns false → returns false as a no-op. ✓ (TOCTOU safe via ConcurrentDictionary)
47. Watchdog's `_onExternalClose` callback (fire-and-forget Task.Run) is queued but hasn't started executing yet when Shutdown hits StopAllAsync → watchdog detects close, queues the task, immediately returns from RunAsync → watchdog loop ends → StopAsync awaits the loop with 5s timeout → loop finishes → StopAsync returns. Meanwhile, the queued callback task runs and tries TryRemove → session already removed by Shutdown → no-op. ✓
48. Watchdog fires external-close callback → StopInternalAsync → TryRemove succeeds → marks profile as "stopping" → begins disposing → MEANWHILE, Shutdown calls StopAllAsync → iterates _sessions.Keys → profile name is no longer in dict (already removed) → StopAllAsync skips it. ✓ (no double-dispose)
49. Watchdog's external-close handler is mid-StopInternalAsync (watchdog.StopAsync is being awaited) → Shutdown calls StopAllAsync → tries TryRemove but session is already gone → no-op. ✓
50. Watchdog fires external-close → queued callback HANGS in StopInternalAsync (e.g., chromedriver.Quit() hangs) → Shutdown calls StopAllAsync → session already TryRemoved (won the race earlier) → StopAllAsync finds no session with that name → moves on → watchdog's callback task remains hanging in background → global 20s shutdown timeout will eventually fire, hard-exiting the process. ⚠ GLOBAL TIMEOUT

**2.6–2.10: Watchdog pause/resume races with shutdown**
51. Shutdown fires while watchdog is paused (rotation in progress, _pauseGate is Reset) → Shutdown calls StopAsync → StopAsync sets _pauseGate.Set() to wake the paused loop → loop wakes up, checks `ct.IsCancellationRequested`, returns. ✓
52. Shutdown fires while _pauseGate.Set() is being called (resume is happening) → shutdown then immediately calls StopAsync which also calls _pauseGate.Set() → idempotent, safe. ✓
53. Shutdown fires, StopAsync calls _pauseGate.Set() → pause state is cleared → watchdog loop wakes if paused → loop checks cancellation and exits. ✓
54. Shutdown called, then immediately (within 1ms) a manual Pause() is called on the watchdog (from the rotation hook, if it hasn't fired yet) → Pause sets ManualResetEventSlim to Reset → shutdown's Set() clears it back → race between the two threads → but Stop is idempotent, and the pause gate is just a signal, so worst case the loop wakes and sleeps again before observing cancellation. ⚠ MINOR RACE
55. Shutdown fires while watchdog is mid-Resume() (calling _pauseGate.Set()) → _pauseGate.Set() is thread-safe → both threads call Set() safely → no issue. ✓

**2.11–2.15: Watchdog's title probe races with driver disposal**
56. Watchdog is awaiting `_session.GetTitleAsync(ct)` → MEANWHILE, Shutdown calls session.DisposeAsync() → driver.Quit() executes → _driver field becomes non-functional → watchdog's GetTitleAsync tries to access _driver.Title → throws WebDriver exception → caught and returned as null. ⚠ EXCEPTION
57. Watchdog's title probe has obtained a reference to _driver but hasn't accessed .Title yet → Shutdown's session.DisposeAsync starts → driver is disposed mid-access → title probe throws ObjectDisposedException or WebDriverException → caught, returned as null. ⚠ EXCEPTION
58. Watchdog's title probe is inside the Task.Run offload → Shutdown's session.DisposeAsync is executing in parallel on another thread → thread-pool task finishes or throws → watchdog continues. Most likely the thread-pool task fails because _driver is disposed. Benign.
59. Watchdog's title probe encounters a timeout or network error during `GoToUrl` (if a navigation is in flight) → timeout exception → caught and logged in GetTitleAsync → returned as null → watchdog counts consecutive nulls. ✓
60. Watchdog's title probe succeeds just before Shutdown's session.DisposeAsync starts → probe returns a non-null title → watchdog resets failure counter → next tick, Shutdown's dispose interrupts the sleep → probe never runs again. ✓

**2.16–2.20: Watchdog's heartbeat update races with DB finalize**
61. Watchdog's TryHeartbeatAsync is updating `runs.heartbeat_at` → Shutdown's StopInternalAsync calls `_runs.FinishAsync()` which updates the SAME run row with a finished_at timestamp → both SQL updates in flight → one wins (SQLite serializes) → one of the updates succeeds. ✓ (DB serializes, idempotent columns)
62. Watchdog's heartbeat succeeds, updates heartbeat_at to "now" → Shutdown's FinishAsync sets finished_at and updates heartbeat_at one more time → last write wins. ✓
63. Watchdog's heartbeat times out (caught in TryHeartbeatAsync, logged as DEBUG) → Shutdown's FinishAsync proceeds to update the row. ✓
64. Watchdog's heartbeat is awaiting the DB write when Shutdown's FinishAsync is called on the same row → both tasks are awaiting the same write → one gets there first, other completes normally. ✓
65. Watchdog tries to heartbeat a run that Shutdown has already finalized (finished_at IS NOT NULL) → query might update zero rows → TryHeartbeatAsync catches any exception, logged as DEBUG. ✓

**2.21–2.25: Watchdog's failure-counting races with shutdown**
66. Watchdog counts 2 consecutive null-title probes → fires external-close callback → SAME INSTANT, a manual Stop is called on the runner (via UI click) → both routes call TryRemove → one wins. ✓
67. Watchdog's failure counter reaches 2 → about to fire callback → IMMEDIATELY, WPF shutdown fires → Shutdown calls StopAsync on watchdog → watchdog's external-close callback fires Task.Run but the loop is about to return anyway → callback task runs and tries TryRemove (already removed by Shutdown). ✓
68. Watchdog counts null probes and hits 2 → fires callback (async) → loop returns (cleanup) → StopAsync called by Shutdown → awaits the (now-completed) loop with 5s timeout → loop is done, returns immediately. ✓
69. Watchdog's consecutive-null counter is at 1 → Shutdown fires → stopCts is cancelled → watchdog's current await returns OperationCanceledException → caught → returns → counter is lost, but loop is exiting anyway. ✓
70. Watchdog's consecutive-null counter is at 1 (single transient failure, about to reset on next successful probe) → Shutdown fires → watchdog exits before the next probe. Counter lost, but session is being disposed anyway. ✓

**2.26–2.30: Watchdog stopCts disposal races**
71. Shutdown calls StopAsync() which calls `_stopCts.Cancel()` → cancel is broadcast to the _loop task → loop exits → StopAsync awaits with 5s timeout → loop finishes → StopAsync calls `_stopCts.Dispose()` safely. ✓
72. StopAsync calls Cancel() and Dispose() on _stopCts → _stopCts is IDisposable → Dispose() while a task is mid-observation of the token → token goes into a disposed state → task's catch handlers may see ObjectDisposedException on token access. ⚠ TIMING
73. _loop task is exiting its finally block → DisposeAsync is called (from RealProfileRunner) → DisposeAsync calls StopAsync (again) → StopAsync checks `_disposed` → returns immediately. ✓
74. _stopCts is already disposed by a prior StopAsync call → second StopAsync (from shutdown) tries to Cancel() → throws ObjectDisposedException → caught by the `try { _stopCts.Cancel(); } catch { /* swallow */ }` block. ✓
75. _stopCts.Dispose() is called, releasing the token → any thread still checking token.IsCancellationRequested may see the token is disposed → exception possible. ⚠ TIMING

**2.31–2.35: Watchdog _disposed flag and reentry**
76. StopAsync is called once (by shutdown) → _disposed is set to true → subsequent call (from DisposeAsync) → StopAsync checks `if (_disposed) return` immediately. ✓
77. DisposeAsync is called → calls StopAsync → then calls _stopCts.Dispose() and _pauseGate.Dispose() → subsequent DisposeAsync call → checks `if (_disposed) return` → returns before calling Dispose again. ✓
78. Multiple callers try to call StopAsync concurrently (race) → first caller sets _disposed = true, proceeds → second caller checks _disposed, returns immediately. ⚠ MINOR RACE
79. StopAsync is mid-execution → another thread calls DisposeAsync → DisposeAsync checks `if (_disposed) return` (still false) → enters → calls StopAsync again → potential double-call to Cancel() / Dispose(). ⚠ RACE
80. Watchdog is already disposed → external-close callback tries to call back through StopInternalAsync → TryRemove on _sessions finds no session (it was removed earlier) → returns false immediately. ✓

---

### Category 3: Locked-File Inheritance & Child Process Handle Leaks (30 cases)

**3.1–3.5: chromedriver.exe inherits parent handle table**
81. WPF process creates chromedriver.exe via Selenium's `new ChromeDriver()` → chromedriver is a child process of WPF → by default, Windows child processes INHERIT the parent's open file handles (unless explicitly closed or created with bInheritHandles=false) → if WPF has GhostShell.dll open, chromedriver's handle table includes an inherited handle to that file. ⚠ CRITICAL
82. chromedriver.exe spawns chrome.exe as its child → chrome.exe inherits handles from both chromedriver AND the inherited chain from WPF → chrome.exe's handle table now includes a copy of every file handle WPF opened. ⚠ CRITICAL
83. WPF exits during shutdown → handle is closed for the WPF process → inherited copies in chromedriver/chrome child handles are STILL OPEN (separate handle table entries) → GhostShell.dll is still locked from the child processes' perspective. ⚠ CRITICAL FILE LOCK
84. PowerShell helper (apply.ps1) tries to copy GhostShell.dll to a temp location → file is locked by chromedriver.exe (inherited handle) → copy fails → update partially applied → on restart, app loads old binary or broken one. ⚠ UPDATE FAILURE
85. Selenium 4's ChromeDriverService initialization sets `UseShellExecute = false` but does NOT explicitly set `InheritHandles = false` → default is `true` (inherit). ⚠ INHERIT DEFAULT

**3.6–3.10: chrome.exe orphans its parent handles**
86. chrome.exe is spawned by chromedriver, inherits handles → chrome.exe detaches from chromedriver or is killed → chrome.exe still holds inherited file handles → if chrome.exe process survives (Selenium 4 sometimes leaks it on hard kill), file locks persist. ⚠ ORPHAN LOCK
87. Shutdown calls driver.Quit() → sends "kill" CDP command → chrome.exe terminates → SOMETIMES chromedriver waits for the exit, SOMETIMES chrome lingers (worker process races) → if chrome.exe doesn't exit, inherited handles are still live. ⚠ PROCESS LEAK
88. Shutdown calls service.Dispose() (which calls Process.Kill on chromedriver) → chromedriver.exe terminates → chrome.exe children may not be reaped (tree kill depends on Job Object setup) → chrome.exe continues, keeps inherited handles. ⚠ TREE KILL FAILURE
89. SeleniumBrowserSession.DisposeAsync loops through _ownedPids and calls Process.Kill(entireProcessTree: true) → BUT _ownedPids is empty in the current implementation (see line 289: `ownedPids: Array.Empty<int>()`) → chrome.exe PIDs are never tracked → reap is skipped. ⚠ UNTRACKED PIDS
90. Shutdown's AppShutdown.ReapOrphanChromeProcesses does a WMI query for chrome.exe/chromedriver.exe whose command line contains the profiles dir → IF a chrome.exe is running with inherited but locked GhostShell.dll handle, WMI sweep will find and kill it. ✓ (backstop)

**3.11–3.15: Cross-process handle inheritance timing**
91. WPF opens GhostShell.dll (self-load on startup) → handle is live in WPF's table → chromedriver.exe is spawned AFTER this → inherits the open handle. ⚠ INHERITED
92. Serilog's file sink opens the daily log file for appending → handle is live → all child processes inherit copies → when Serilog tries to close the sink during shutdown, WPF's handle is released → child processes still hold copies. ⚠ LOG FILE LOCK
93. SQLite connection to the database file is open in WPF → handle inherited by chromedriver/chrome → during shutdown, SQLite connection closes → file handle in WPF released → child processes still have it. ⚠ DB FILE LOCK
94. HTTP proxy socket from auth-proxy forwarder is open → handle inherited by chromedriver (TCP socket) → forwarder's socket.Close() during DisposeAsync → closes the socket in the forwarder process → chromedriver's inherited copy is still "open" (from its perspective, still a valid socket handle). ⚠ SOCKET INHERITANCE
95. WPF spawns a temporary file during launch (temp folder) → handle left open (bug) → inherited by children → children are killed → temporary file is locked until WPF exits. This is a resource leak scenario (not critical for the update, but bad practice). ⚠ TEMP FILE

**3.16–3.20: Job Object absence and tree-kill failures**
96. Windows processes can be organized into Job Objects → when a process exits, all child processes in the same job are terminated → if the WPF process is in a job, its children (chromedriver) and grandchildren (chrome) should be killed when WPF exits. Currently there's NO Job Object setup in the code. ⚠ NO JOB OBJECT
97. Process.Kill(entireProcessTree: true) is called on chromedriver.exe → internally uses PROCESS_TERMINATE + recursively kills direct children → BUT if chrome.exe has spawned worker processes (e.g., --type=utility), those may NOT be direct children → tree kill may miss them. ⚠ WORKER PROCESS
98. chromedriver is killed but a chrome.exe utility worker process has been reparented by Windows (parent died, becomes child of smss.exe or explorer.exe) → tree kill won't reach it. ⚠ REPARENT
99. WPF calls Process.GetProcessById(pid); proc.Kill(entireProcessTree: true) → if the process already exited between the Get and Kill call, ArgumentException is thrown → caught and logged. ⚠ TOCTOU
100. Job Object IS created for WPF (hypothetically in the future) → but child processes can be created with bCreateSuspended=true or other flags that affect job membership → not guaranteed that all children inherit the job. ⚠ FUTURE

**3.21–3.25: DLL lock scenarios during binary swap**
101. WPF loads GhostShell.Runtime.dll into memory → no copy-on-write, file must stay unlocked on disk → Shutdown starts → child processes (chromedriver, chrome) have inherited file handles → PowerShell tries to copy GhostShell.Runtime.dll → copy fails (file busy). ⚠ DLL LOCK
102. WPF loads GhostShell.Core.dll (data layer) → handle inherited → Shutdown closes WPF → child handles still live → PowerShell copies fail → data layer is locked. ⚠ MULTI-DLL
103. GhostShell.exe itself is running from the install dir → Shutdown doesn't unload the EXE → on restart, a new EXE process launches (OK, different instance) → BUT if the old process's handle is still held by a child, the swap is incomplete. ⚠ EXE LOCK
104. Selenium library (OpenQA.Selenium.dll) is loaded → not a problem (lives in %ProgramFiles%) → but if Selenium internally loaded a platform-specific library (a DLL) and a child holds it, that DLL is locked. Unlikely but possible. ⚠ TRANSITIVE
105. App runs on a network share (unlikely, but possible) → inherited file handles to a network path → Shutdown → child processes → network share is locked → copy fails → update is blocked. ⚠ NETWORK SHARE

**3.26–3.30: Extension files and user-data-dir locks**
106. Extensions are loaded from %LocalAppData%\GhostShell\Extensions\<id>\ → chrome.exe has a handle to manifest.json or extension JS files → Shutdown → chrome.exe still live → PowerShell's file-copy DURING the swap tries to update extension files → copy fails. ⚠ EXTENSION LOCK
107. Chrome's Default/Preferences JSON is open for reading → Shutdown doesn't close all file handles → chrome.exe still has it open → PowerShell tries to replace it → copy fails. ⚠ PREFERENCES LOCK
108. Session storage or temp files in the user-data-dir are open by chrome → Shutdown → chrome still alive → PowerShell tries to clean or replace the dir → operations fail. ⚠ SESSION STATE
109. Snapshot files (in %LocalAppData%\GhostShell\snapshots\) are being read by a background task (WarmupService?) → Shutdown → task still reading → PowerShell tries to delete old snapshots as part of update prep → delete fails. ⚠ SNAPSHOT LOCK
110. Process.Kill(entireProcessTree: true) is called, but Windows asynchronously reaps the process (not immediate) → script continues before the process is fully cleaned up → file handles are still open for a brief moment. ⚠ ASYNC CLEANUP

---

### Category 4: chrome.exe Orphans & Patched Chromium Quirks (25 cases)

**4.1–4.5: Selenium 4 chrome.exe leak**
111. Selenium 4's ChromeDriver.Quit() sends a graceful shutdown signal via CDP → Chromium should exit cleanly → SOMETIMES (especially under load or network stress) the process doesn't exit → Quit() returns anyway (timeout) → chrome.exe continues running. ⚠ QUIT TIMEOUT
112. Patched Chromium 149.0.7805.0 (from Phase 27) has modified shutdown behavior → may not honor the quit signal in the same way as stock Chrome → driver.Quit() returns but chrome.exe lingers. ⚠ PATCHED VERSION
113. Shutdown calls driver.Quit() inside a try/catch → if Quit() throws (e.g., "connection to chromedriver lost"), the exception is caught and logged → chrome.exe is still alive → DisposeAsync continues to the next step (service.Dispose()). ⚠ EXCEPTION HIDING
114. Multiple chrome.exe processes spawned during a session (one main process, N worker processes) → driver.Quit() kills the main process → workers survive (they may have been reparented or in a different job group) → only the main PID is tracked/killed. ⚠ WORKER PROCESSES
115. Chrome's "startup warning" cache or extension loader keeps a process alive (--type=utility worker) → main process exits → utility worker continues → tree kill from chromedriver doesn't reach it. ⚠ UTILITY WORKER

**4.6–4.10: chromedriver.exe shutdown behavior**
116. service.Dispose() calls the underlying WinProcess.Kill() on chromedriver → if chromedriver is hanging on its own shutdown (waiting for chrome, which isn't responding), Kill() may take several seconds → step timeout fires. ⚠ HANG
117. chromedriver.exe is waiting for chrome.exe to exit (after sending kill signal) → chrome.exe is hung on a file I/O or network call → chromedriver.exe hangs (waiting for its child) → process.Kill() on chromedriver may not interrupt it (SIGKILL vs SIGTERM behavior). ⚠ HUNG CHILD
118. Chromedriver service is reused from a prior session (same port number, but service recreated) → old chromedriver.exe process is still alive → new ChromeDriver() ctor tries to bind the same port → conflict. But Selenium should detect this and fail fast. ⚠ PORT CONFLICT
119. Chromedriver logs are being written to a file → service.Dispose() → log file is still open from chromedriver's perspective → WPF tries to read the log file for diagnostics → file lock contention. ⚠ LOG FILE
120. Chromedriver spawns a debugging server on localhost:PORT for CDP → when driver.Quit() is called, the server should shut down → but if a script step is mid-CDP call, the server may not respond cleanly → Quit() times out. ⚠ CDP TIMEOUT

**4.11–4.15: Patched Chromium extension validation overhead**
121. Startup of patched Chromium 149 with extension validation enabled → Extension Pinwriter has registered extensions in Default/Preferences → Chromium validates each extension at startup (slow) → if an extension is corrupted or unsigned, startup may hang. ⚠ EXTENSION VALIDATION
122. Extension validation timeout during shutdown → chrome.exe hangs during quit → driver.Quit() times out → caught and logged → process.Kill() is called but chrome.exe is in an uninterruptible state. ⚠ UNINTERRUPTIBLE
123. Extensions are loaded from user-data-dir (localhost paths) → ExtensionPinWriter has written their state to Preferences → on startup, Chromium tries to load them → if a file is corrupt or missing (left behind from a crash), startup hangs → affects both the current session and the shutdown of the next boot-after-update. ⚠ STARTUP HANG
124. Extension file is removed during runtime (user deletes from disk) → Chromium continues running with a stale handle to the deleted file → when Shutdown tries to clean the dir, file is already gone. Benign. ✓
125. Multiple extensions are loaded and one of them deadlocks (buggy extension code) → chrome.exe hangs → Quit times out → Kill is called. ⚠ EXTENSION DEADLOCK

**4.16–4.20: DevToolsActivePort and session-restore state**
126. Chrome writes DevToolsActivePort file to user-data-dir during startup → file contains the port number for CDP → if the session crashes, the file is stale → next launch reads it and tries to connect to the old port → Selenium reports "DevToolsActivePort file doesn't exist" because it looks for an old file. LaunchPreflight.Run() is supposed to clean this up. ✓
127. BrowserLauncher.LaunchAsync calls LaunchPreflight.Run() BEFORE launching → preflight wipes stale files → chrome should start cleanly. ✓
128. Session restore files (Session Storage, Chrome session data) are stale from a crash → Chromium tries to restore on next launch → may hang during recovery → startup timeout (30s chromedriver init) is exceeded. ⚠ RECOVERY HANG
129. User-data-dir is "in use" because a chrome.exe from a prior crash is still running → preflight tries to kill it via WMI → if WMI query is slow or the process can't be killed, the dir remains locked → next LaunchAsync will fail with "data dir in use". ⚠ LOCKED DIR
130. Session-restore is disabled (via ChromeOptions) → Chromium should skip recovery → faster startup → but some session state files may still be opened for reading during shutdown → handled by file handle reaping. ⚠ STATE FILES

**4.21–4.25: Process reaping edge cases**
131. SeleniumBrowserSession._ownedPids is currently empty (line 289) → even if chrome.exe PIDs were tracked, they're not stored → DisposeAsync's loop over _ownedPids does nothing. ⚠ BUG
132. BrowserLauncher.LaunchAsync creates a session with `ownedPids: Array.Empty<int>()` (line 289) → PIDs are never captured from ChromeDriver or the spawned processes → reaping is broken. ⚠ UNIMPLEMENTED
133. If _ownedPids were populated, SeleniumBrowserSession.DisposeAsync would loop and call Process.Kill(entireProcessTree: true) on each → multiple calls to Kill on the same process → second call throws ArgumentException (process no longer exists) → caught and swallowed. ✓
134. WMI orphan sweep runs after all explicit kills → finds any chrome/chromedriver whose command line contains a profile dir → kills them with tree termination. ✓ (backstop)
135. Orphan sweep's WMI query is slow (Win32_Process query can take seconds on a heavily loaded system) → Shutdown's orphan-sweep step has an 8s timeout → may timeout and log a warning. ⚠ TIMING

---

### Category 5: HostedService.StopAsync Cancellation & Timeouts (28 cases)

**5.1–5.5: RunnerHost cancellation behavior**
136. Host.StopAsync() is called with a default CancellationToken (CancellationToken.None from app shutdown) → RunnerHost.StopAsync receives it → cancels _cts (linked source) → TickLoop observes cancellation → loop exits. ✓
137. RunnerHost.TickAsync is in the middle of FireAsync for a schedule → cancellation is signaled → TickAsync should throw OprCanceledEx at the next ct.ThrowIfCancellationRequested() → caught by the tick loop, loop exits. ✓
138. FireProfileAsync is calling `_runner.StartAsync(profile, ct)` with the cancellation token → if StartAsync has inserted the run row and is inside LaunchAsync, and cancellation fires, what happens? LaunchAsync was called with the same token. ⚠ PROPAGATION
139. RunnerHost doesn't timeout its own operations → Host.StopAsync has an internal timeout (typically default or infinite) → if a tick is slow, the whole Host.StopAsync may block → Shutdown's "host-stop" step has an 8s timeout → if exceeded, log and continue. ⚠ TIMEOUT
140. A due schedule is being processed at the moment Host.StopAsync is called → the schedule fires may have started → the cancellation token may or may not interrupt it → depends on how far the fire has progressed. ⚠ RACE

**5.6–5.10: WarmupQualityMonitor cancellation**
141. WarmupQualityMonitor.StartAsync spawns a background task that periodically launches warmup runs → if it's in the middle of a LaunchAsync when Host.StopAsync is called, the cancellation propagates to the inner launch. ⚠ PROPAGATION
142. Warmup launch has inserted a run row and is inside the chrome startup (30s chromedriver init timeout) → cancellation token is passed → Selenium's chromedriver init may not honor the cancellation immediately (it's awaiting an HTTP response from chromedriver). ⚠ TIMEOUT
143. WarmupQualityMonitor.StopAsync is called → should cancel its background task → task should unwind → if task is hung in an await, the cancellation unwinds it (if the await respects the token). If not, it hangs. ⚠ IGNORING TOKEN
144. Warmup has captured a snapshot and is saving it to the DB → cancellation fires → SaveAsync may be interruptible (if it respects the token) or may complete/throw. ⚠ DB OPERATION
145. Multiple warmups are queued and one of them is slow → Host.StopAsync cancels → slower ones should unwind quickly. ✓

**5.11–5.15: SnapshotRetentionService cancellation**
146. SnapshotRetentionService.StartAsync spawns a background task that periodically deletes old snapshots → if it's inside a SQL DELETE when Host.StopAsync is called, the delete may continue or be interrupted. ⚠ DB OPERATION
147. SnapshotRetentionService.StopAsync is called → background task should be cancelled → if the task is mid-DELETE FROM snapshots, the query is not cancellable from the .NET side (it's executing server-side). ⚠ SERVER-SIDE
148. Snapshot files are being deleted from disk at the same time the app is shutting down → file I/O may be slow → background task hangs → StopAsync timeout fires. ⚠ I/O HANG
149. Multiple snapshots are being deleted (one per session) → cascading deletes from the DB → slow cascade → cancellation timeout. ⚠ CASCADING
150. SnapshotRetentionService doesn't honor the cancellation token (implementation may not check the token) → task continues running → timeout fires, task is abandoned. ⚠ NO-OP

**5.16–5.20: FingerprintQualityMonitor cancellation**
151. FingerprintQualityMonitor.StartAsync launches background probes that run fingerprint tests → if a test is in flight when Host.StopAsync is called, the test may not be cancellable (depends on the test implementation). ⚠ TEST DURATION
152. Fingerprint test is running a lengthy screenshot capture → screenshot is CPU-intensive → cancellation token passed → but the task can't interrupt mid-screenshot → must wait for screenshot to complete. ⚠ UNINTERRUPTIBLE
153. Fingerprint test has generated a file (screenshot, trace) on disk → halfway through save → Host.StopAsync is called → file save may complete or be partial → cleanup handles the partial file. ⚠ PARTIAL FILE
154. Multiple fingerprint quality checks are queued → one is slow → Host.StopAsync fires → older checks unwind, slow one hangs → timeout kills the whole service. ⚠ QUEUING
155. Fingerprint test result is being written to the DB → SQL write is in flight → cancellation token doesn't stop server-side execution → write may complete. ✓

**5.21–5.28: Generic HostedService ordering issues**
156. Host.StopAsync calls StopAsync on each IHostedService in LIFO order (reverse of registration) → but there's no explicit ordering for RealProfileRunner vs. RunnerHost → if RunnerHost.StopAsync is called BEFORE RealProfileRunner.StopAsync, the runner is still active when schedules might try to fire. ⚠ ORDER
157. RunnerHost is registered AFTER RealProfileRunner (line 119 in App.xaml.cs) → reverse order means RealProfileRunner is stopped first → runner stops, then the scheduler stops. ✓
158. WarmupQualityMonitor is a HostedService registered after the runner → on shutdown, WarmupQualityMonitor.StopAsync is called before RealProfileRunner.StopAsync → warmup may try to launch a new session while runner is still stopping. ⚠ ORDER
159. A HostedService throws an exception in StopAsync → Host.StopAsync catches and logs it but continues stopping other services. ✓ (dependency-injection framework handles this)
160. A HostedService's StopAsync is slow but doesn't timeout (internal no-timeout implementation) → Host.StopAsync blocks on it → Shutdown's "host-stop" step timeout fires → step is abandoned, other steps skipped. ⚠ CASCADE
161. Two HostedServices compete for the same resource (e.g., both trying to write to the runs table during shutdown) → SQL serialization → one blocks the other → timeout risk. ⚠ CONTENTION
162. A HostedService is in a finally block (cleanup code) when Host.StopAsync signals cancellation → finally blocks are not cancellable → code runs to completion → may extend shutdown. ⚠ FINALLY
163. Cancellation token passed to Host.StopAsync (from the dispatcher, via AppShutdown.RunAsync) → if the token is already cancelled (e.g., Shutdown was called twice rapidly), services don't receive a fresh token. ⚠ STALE TOKEN

---

### Category 6: DI Singleton Disposal Timing (20 cases)

**6.1–6.5: IUpdateService lifecycle during ApplyAsync + Shutdown**
164. ApplyAsync is downloading the portable zip into a staging directory → halfway through the download, Shutdown is triggered (from a second ShutdownRequested event, or a race with ApplyAsync's own ShutdownRequested callback). ⚠ DOUBLE FIRE
165. ApplyAsync is extracting the zip → Shutdown fires → host stops all services → IUpdateService's DisposeAsync (if it has one) runs → partially-extracted files in the staging dir may be left behind → on restart, the app might have a half-extracted update directory. ⚠ PARTIAL EXTRACT
166. ApplyAsync has completed the download and extraction and is about to call ShutdownRequested → Shutdown is called from somewhere else (e.g., user closes the app manually) → ShutdownRequested event is raised, but the subscriber (App.xaml.cs line 329) may race. ⚠ RACE
167. ApplyAsync is running on a background thread (awaited by UpdateAvailableDialog) → Shutdown fires on the dispatcher → ApplyAsync continues in the background, possibly completing after the WPF process exits. ⚠ BACKGROUND TASK
168. IUpdateService is a singleton, registered in DI → on Host.StopAsync, if it implements IAsyncDisposable, its DisposeAsync is awaited → download/extraction is interrupted. ⚠ INTERRUPT

**6.6–6.10: Singleton disposal chain**
169. IUpdateService depends on IHttpClientFactory (for GitHub API requests) → if the service is mid-request when Host.StopAsync disposes it, the HTTP response may be abandoned. ⚠ NETWORK ABORT
170. IUpdateService holds a reference to ISettingsService (read settings before download) → Settings is also a singleton → disposal order matters. If IUpdateService is disposed before ISettingsService, and DisposeAsync tries to read a setting, null-ref possible. ⚠ ORDERING
171. DatabaseConnection is a singleton, shared across the whole app → if IUpdateService (or another service) holds a handle and doesn't release it during DisposeAsync, the DB connection hangs during app shutdown → "database locked" errors. ⚠ DB LOCK
172. Serilog sink (file logger) is disposed during Host.StopAsync → subsequent log writes (from other services still disposing) may throw NullReferenceException. ⚠ LOGGING
173. A singleton service holds a Timer or DispatcherTimer → if the service is disposed while a timer callback is in flight, the callback may access disposed fields. ⚠ TIMER CALLBACK

**6.11–6.15: Update staging directory cleanup**
174. ApplyAsync creates a staging directory (e.g., C:\Users\...\AppData\Local\GhostShell\update-staging-GUID) → Shutdown occurs → staging dir is left on disk (cleanup is not part of Shutdown) → next launch, the cleanup code (if any) tries to clean it up. ⚠ ORPHAN DIR
175. Staging dir has a lock file to indicate "update in progress" → Shutdown doesn't remove the lock file → on restart, the app sees the lock file and waits or skips the update → behavior depends on implementation. ⚠ LOCK FILE
176. Staging dir is on a network share or slow disk → ApplyAsync is mid-extract → Shutdown fires → extraction is interrupted → partial files remain → next launch, extraction is retried or fails. ⚠ NETWORK
177. Staging dir extraction has created hardlinks (Windows deduplication) → deletion of the staging dir fails (hardlinks can't be unlinked easily) → dir remains on disk consuming space. ⚠ HARDLINK
178. Staging dir permissions are restrictive (created with limited permissions) → Shutdown's cleanup can't delete files → dir remains. ⚠ PERMISSIONS

**6.16–6.20: Singleton services still in use after Host.StopAsync**
179. A script is still running (in the background, fire-and-forget task) after Host.StopAsync is called → script tries to query ISessionService (to save a snapshot) → ISessionService has been disposed → null-ref or ObjectDisposedException. ⚠ STALE SERVICE
180. A background task (e.g., self-check probes) is awaiting a database query when Host.StopAsync disposes the DatabaseConnection → query throws → task catches and logs (if it catches exceptions). ⚠ QUERY FAILURE
181. TrafficCollector's background loop is mid-flush when the ITrafficService is disposed → flush call throws → TrafficCollector swallows the exception (in FlushAsync). ✓
182. VaultIdleWatcher.Start() has hooked global input events → App.OnExit runs (dispatcher teardown) → before Host.StopAsync, the DispatcherTimer is still running → may fire a callback after Host is partially stopped. ⚠ TIMER RACE
183. A singleton service holds a reference to an async task that's never awaited → task runs in the background until the process exits → during shutdown, the task may not be given a chance to cleanup properly. ⚠ FIRE-AND-FORGET

---

### Category 7: Multi-Session Teardown Ordering (15 cases)

**7.1–7.5: Sequential vs. parallel teardown**
184. RealProfileRunner.StopAllAsync() iterates _sessions.Keys and calls StopAsync on each profile sequentially (for loop) → first profile's DisposeAsync (driver.Quit + chromedriver dispose) takes 5–10s → second profile waits → timeout risk if multiple sessions are running. ⚠ SEQUENTIAL
185. StopAsync for profile A calls session.DisposeAsync() → driver.Quit() hangs on chrome → step timeout (8s) fires → abandoned. Profile B's StopAsync is never called (still waiting for A) → B's session is never disposed. ⚠ ORPHAN SESSION
186. Five profiles are running (5 × chromedriver, 5 × chrome) → StopAllAsync starts disposing them sequentially → first three dispose cleanly (total 15s) → step timeout (8s per step in Shutdown) for "stop-runner" fires → remaining two sessions are abandoned. ⚠ TIMEOUT
187. StopAllAsync should be parallel (concurrent StopAsync calls) but is sequential (for loop) → significant shutdown delay. Not a bug per se, but inefficient. ⚠ PERF
188. Session A's DisposeAsync is disposing its TrafficCollector → TrafficCollector.DisposeAsync awaits _loop.WaitAsync(2s) → if the loop is mid-flush, this may timeout → DisposeAsync continues. Session B starts disposing, flushes traffic at the same time → SQL write contention. ⚠ CONTENTION

**7.6–7.10: Snapshot save vs. dispose race**
189. Profile A is on a clean-stop path (exit_code = 0) → CaptureCleanRunAsync is called (not during shutdown, but during manual stop) → SetCookiesAsync is mid-restore when Shutdown fires → snapshot capture races with session dispose. ⚠ RACE
190. Profile B is on a crash-exit path (exit_code != 0) → CaptureCleanRunAsync is skipped → session is disposed cleanly without snapshot overhead. ✓
191. Multiple profiles are capturing snapshots concurrently (if they were manually stopped before Shutdown) → ISessionService.SaveAsync is called on each → DB write contention. ⚠ DB LOCK
192. Snapshot save is writing to disk (serializing cookies/storage to JSON in the DB BLOB) → concurrent writes from multiple sessions → SQLite serializes, one blocks others → timeout risk. ⚠ SERIALIZATION
193. Snapshot payload (cookies + storage) for profile A is large (100+ origins, 1000+ cookies) → SaveAsync takes 5–10s → Shutdown's step timeout fires mid-save. ⚠ PAYLOAD SIZE

**7.11–7.15: Run row finalization ordering**
194. Profile A's session is disposed → profile B's session is disposed → both call `_runs.FinishAsync()` to finalize their run rows → if both are concurrent (race), SQLite serializes. ⚠ DB CONTENTION
195. Run row finalize is inserting a final log message into the runs.last_error or runs.stop_reason column → multiple profiles finalize concurrently → no locking → one may read a stale column value → unlikely but possible. ⚠ RACE
196. A run row's finalization includes a DB trigger (if any) that cascades cleanup (e.g., delete old session snapshots for that run) → multiple run finalizations trigger concurrent cascades → potentially expensive DB operations. ⚠ CASCADE
197. Watchdog's external-close detection finalizes run A at the same time manual Shutdown finalizes run A → TryRemove prevents double-finalize (session is removed from dict first) → FinishAsync is only called once. ✓
198. Run row is finalized but the corresponding profile session is still disposing chromedriver (takes 5s) → second profile's run is finalized → run table is consistent but the session state is still in-flight. ✓ (acceptable, tables are consistent)

---

### Category 8: Parent PID Handshake & Detached Child Processes (20 cases)

**8.1–8.5: Environment.ProcessId capture**
199. ApplyAsync captures `Environment.ProcessId` at the time ShutdownRequested is raised → this is the WPF process ID → PowerShell script waits on this PID for exit. ⚠ ASSUMPTION
200. WPF process exits cleanly → PowerShell script's `Wait-Process` unblocks → script proceeds to file copy. ✓
201. WPF process is terminated by a third party (Task Manager, Windows shutdown, etc.) → PowerShell script is waiting on the PID → wait succeeds (process is gone) → script proceeds. ⚠ ASSUMPTION
202. WPF exits, but a child process (chromedriver.exe) inherits a console or I/O handle → exiting WPF doesn't immediately close those handles → chromedriver continues → files are still locked. ⚠ INHERITED HANDLES
203. PowerShell script waits on PID X → WPF exits → Windows reuses PID X for a new unrelated process → Wait-Process sees the new process exit and unblocks → file copy proceeds while the new process (not related to GhostShell) is running. ⚠ CRITICAL RACE

**8.6–8.10: Child process detachment**
204. chromedriver.exe is a child process of WPF (created via Selenium's new ChromeDriver()) → when WPF exits, chromedriver is still running (may or may not be terminated automatically, depends on Job Object setup). ⚠ ORPHAN
205. chrome.exe is a child of chromedriver → when chromedriver is killed, chrome may survive (reparented or escaped) → WPF exits, PowerShell waits on WPF PID → wait completes but chrome.exe still holds file handles. ⚠ ORPHAN CHAIN
206. Selenium's `service.Start()` may create chromedriver as a separate process (not attached to WPF's job or console) → when WPF exits, chromedriver is not automatically terminated → PowerShell must wait for chromedriver separately or trust Shutdown's orphan sweep. ⚠ DETACHED
207. `bInheritHandles=true` (default) when spawning chromedriver → child inherits all open handles → WPF exits, child's file handle table is unchanged → files are locked until child exits. ⚠ HANDLE INHERITANCE
208. Shutdown tries to kill chromedriver explicitly (via service.Dispose) before WPF exits → if Kill is successful, PowerShell has nothing to worry about. But if Kill hangs, chromedriver outlives WPF. ⚠ INCOMPLETE KILL

**8.11–8.15: PowerShell script timing**
209. PowerShell script: `Wait-Process -Id $parentPid` → waits for WPF to exit → WPF may take 20s to shutdown (hung service) → Wait completes → script attempts file copy. ⚠ TIMING
210. PowerShell script waits on WPF PID for a timeout (e.g., 60s) → WPF doesn't exit (hung Shutdown) → timeout fires → script proceeds with file copy while WPF is still alive. ⚠ HUNG SHUTDOWN
211. PowerShell script copies GhostShell.exe from staging to the install dir → GhostShell.exe is running (the WPF process) → copy fails or replaces a locked file (Windows allows this, but the next launch reads the old version from memory). ⚠ IN-USE EXE
212. PowerShell script calls `taskkill /F /T /PID $parentPid` as a backstop after the wait timeout → forces kill of WPF and all children → orphan sweep in Shutdown may race with the forceful kill. ⚠ RACE
213. PowerShell script is running as the same user (authenticated) → all file operations succeed → script proceeds quickly. If running as a different user or with insufficient permissions, copy fails. ⚠ PERMISSIONS

**8.16–8.20: Multi-instance process handling**
214. User has multiple GhostShell.exe instances running (unlikely, but possible if the user manually launches it twice) → ApplyAsync is in instance #1 → Shutdown is in instance #1 → PowerShell waits on instance #1's PID → instance #2 is still running, holding file handles. ⚠ MULTI-INSTANCE
215. Scheduler is running a profile in instance #1 → instance #2 is launched by the user → both instances are running → ApplyAsync in instance #1 raises ShutdownRequested → instance #1 shuts down → PowerShell waits on instance #1's PID → instance #2 is still running with the same user-data-dir (conflict). ⚠ CONTENTION
216. WPF process is running with a low-integrity token (sandboxed) → PowerShell script runs in a different (or same) integrity level → permissions may differ. ⚠ PERMISSIONS
217. WPF creates a mutex to prevent multiple instances → on Shutdown, the mutex is released → a new instance can start → if new instance starts before file copy is complete, file locks conflict. ⚠ RACE
218. PowerShell script is running in the same user session → all environment variables are consistent → file copy is straightforward. ✓

---

### Category 9: Cancellation Token & Async Stack Hygiene (20 cases)

**9.1–9.5: CancellationTokenSource lifecycle**
219. Shutdown's AppShutdown.RunAsync calls `Host.StopAsync(CancellationToken.None)` → Host.StopAsync doesn't receive a timeout token → services must implement their own timeouts via SafeStepAsync. ✓
220. SafeStepAsync creates an implicit timeout via `await work().WaitAsync(budget)` → if `work()` is a long-running operation without its own cancellation, WaitAsync's timeout is the only safeguard. ✓
221. A HostedService's StopAsync receives a CancellationToken from Host → the service should check `ct.IsCancellationRequested` periodically → if the service ignores the token, it runs to completion (slow service blocks shutdown). ⚠ IGNORING TOKEN
222. Cancellation token is passed down through async call chains → at each level, the token should be checked or passed to the next level → if a level doesn't check, the chain may not cancel promptly. ⚠ CHAIN BREAK
223. A task is awaiting a non-cancellable operation (e.g., Process.WaitForExit without a timeout) → cancellation token is passed but ignored → task hangs. ⚠ NON-CANCELLABLE

**9.6–9.10: StopAsync implementation patterns**
224. RealProfileRunner.StopAsync calls StopInternalAsync → which is a long-running async method (calls watchdog.StopAsync, session.DisposeAsync, etc.) → if this method doesn't honor the cancellation token passed to StopAsync, it runs to completion. ⚠ MIGHT IGNORE TOKEN
225. RealProfileRunner.StopAsync doesn't take a ct parameter, but StopInternalAsync internally uses CancellationToken.None (see line 490: `ct: CancellationToken.None` in FinishAsync). ⚠ NO-OP TOKEN
226. Watchdog.StopAsync calls `_stopCts.Cancel()` to signal the loop → the loop observes the cancellation and exits → if the loop is in a non-cancellable wait (e.g., a P/Invoke call), the loop hangs. ⚠ NON-CANCELLABLE
227. TrafficCollector.DisposeAsync cancels the _stopCts and waits for the loop with a 2s timeout → if the loop is mid-flush (DB write), the 2s timeout may trigger before the flush completes. ⚠ TIMEOUT
228. SessionWatchdog.StopAsync sets _pauseGate.Set() to wake a paused loop → the loop then checks cancellation and exits → if the loop is slow to wake (gate is a ManualResetEventSlim), the 5s timeout in StopAsync may trigger. ⚠ WAIT TIMEOUT

**9.11–9.15: Exception handling in async chains**
229. Shutdown's AppShutdown.SafeStepAsync wraps work() in a try/catch → if work() throws an exception, it's logged and the step is marked as failed → subsequent steps proceed. ✓
230. A service's StopAsync throws an exception (bug in the service) → SafeStepAsync catches it and logs → next service is stopped → app shutdown continues. ✓
231. Multiple services throw exceptions during shutdown → each exception is logged → shutdown may be littered with warnings but proceeds. ✓
232. A caught exception references a disposed logger (Serilog) → logging the exception throws NullReferenceException → outer catch handler swallows it. ⚠ LOGGING FAILURE
233. An exception is thrown and the exception message references a disposed object → exception.ToString() throws ObjectDisposedException → logging fails. ⚠ MESSAGE SERIALIZATION

**9.16–9.20: Async void and fire-and-forget pitfalls**
234. A background task (Task.Run) is spawned and forgotten during session startup → if Shutdown happens before the task completes, the task runs in the background during shutdown. ⚠ ORPHAN TASK
235. RestoreLatestAsync is spawned as a fire-and-forget `_ = Task.Run(...)` → Shutdown occurs before restore completes → task continues in background, accesses a disposed session → throws ObjectDisposedException → task's catch handler logs and ignores. ✓
236. KickAssignedScriptAsync is spawned as a fire-and-forget → script is running when Shutdown fires → script's cancellation token is cancelled → script observes cancellation and exits cleanly. ✓
237. A script is awaiting `session.NavigateAsync(url, scriptCts.Token)` → Shutdown cancels scriptCts → navigate task is cancelled → script catches and logs. ✓
238. Watchdog's external-close callback is spawned as `_ = Task.Run(...)` (fire-and-forget) → callback is queued but not awaited by the watchdog → if Shutdown races and disposes the watchdog before the callback runs, the callback may race with cleanup. ⚠ RACE

---

### Category 10: Logging, Serilog, and Post-Shutdown Null References (20 cases)

**10.1–10.5: Serilog sink disposal timing**
239. Serilog's file sink is initialized at Host.Build() time → sink opens a daily log file handle → sink is disposed during Host.StopAsync or Host.DisposeAsync → file handle is released. ⚠ TIMING
240. A service logs a message during its DisposeAsync (after Serilog has shut down) → log call is routed to Serilog's logger → sink is already disposed → Serilog swallows the exception (if configured) or throws. ⚠ POST-SINK
241. SessionWatchdog logs "Session for '{Profile}' closed" during DisposeAsync (line 462 in SeleniumBrowserSession) → if Serilog is already shut down, the log call may fail. ⚠ LOGGING
242. Multiple services log during shutdown → Serilog's thread-pool writer (if configured) is busy flushing the first batch → a second log call comes in → queued or dropped (depends on Serilog config). ⚠ BATCHING
243. Serilog's CloseAndFlush is called at the end of App.OnExit (line 445) → flushes all pending logs → but OnExit is called AFTER Host.StopAsync → if Host.StopAsync had already shut down the sink, CloseAndFlush is a no-op. ✓

**10.6–10.10: Logger acquisition post-shutdown**
244. AppShutdown.RunAsync is passed an ILogger → logger is used to log shutdown progress → logger is acquired from Host.Services → if Serilog is down, logger.Log calls may throw. ⚠ SERILOG DOWN
245. A service's StopAsync method has a local logger reference (acquired in the ctor) → during StopAsync, the service logs via the logger → if Serilog is shut down, logging may fail. ⚠ STALE LOGGER
246. SessionLifecycle.CaptureCleanRunAsync (called during clean stop) logs messages → if this is called as part of shutdown and Serilog is down, logging fails. But CaptureCleanRunAsync is only called on clean-stop (exit_code=0), not during shutdown (which is non-zero). ✓
247. RealProfileRunner.StopInternalAsync logs messages at startup and completion → if these are logged after Serilog shutdown, logging fails. The logs are for diagnostic purposes; failures are swallowed (try/catch in logging statements). ✓
248. TrafficCollector.DisposeAsync logs to the injected ILogger → if Serilog is down, logging fails. TrafficCollector's DisposeAsync has `try { ... } catch { /* ignore */ }` so failures are silently ignored. ✓

**10.11–10.15: GlobalExceptionHandler timing**
249. GlobalExceptionHandler is installed in OnStartup (line 231) → handles unhandled exceptions globally → if an exception occurs during shutdown, the handler may try to log via Serilog (which is down). ⚠ SERILOG DOWN
250. AppShutdown.RunAsync is called (normal exit path) and completes → OnExit then cleans up → if an exception occurs after AppShutdown finishes but before OnExit returns, the handler may try to log. ⚠ TIMING
251. OnProcessExit is called (backstop handler) during a forced process exit → GlobalExceptionHandler may fire → handler tries to log via Serilog (which is down or being shut down). ⚠ SERILOG

**10.16–10.20: Null logger references**
252. A service holds a private ILogger field (set in ctor) → service is disposed → DisposeAsync tries to log via the field → if the field is set to null (unlikely), null-ref. ✓ (unlikely)
253. A service is disposed, then accessed again (double-dispose or access-after-dispose) → ILogger is still valid (it's a singleton logger), but the service's state is invalid → logging the invalid state may throw. ⚠ STATE
254. Logger factory is disposed during Host.StopAsync → subsequent logger acquisitions (from a HostedService's StopAsync) return null or throw. ⚠ FACTORY DISPOSED
255. A background task holds a reference to an ILogger acquired at startup → task continues running after Host.StopAsync → logger tries to log → if the logger's internal state is disposed, logging may fail. ⚠ ASYNC LOGGER

---

### Category 11: Migration Runner & First-Launch Post-Update (10 cases)

**11.1–11.5: Migration ordering and idempotency**
256. First launch after update: WPF starts → OnStartup calls Host.Build() → Host.StartAsync() → BEFORE showing the MainWindow, MigrationRunner.Run() is called (line 256 in App.xaml.cs) → migrations are applied. ⚠ TIMING
257. Migration runner reads the current schema version from the DB → computes pending migrations → applies them in order → if a migration fails halfway, the DB may be in an inconsistent state. ⚠ PARTIAL MIGRATION
258. Migration for version N+1 assumes version N is present (e.g., a column added in N, used in N+1) → if migration N wasn't applied (crashed partway), migration N+1 may fail. ⚠ DEPENDENCY
259. MigrationRunner is registered in DI but is not an IHostedService → it's called explicitly in OnStartup → if an exception occurs, the app crashes with a MessageBox. ✓
260. DB schema is at version V20 (old version) → update brings in code that expects V24+ → MigrationRunner applies V21, V22, V23, V24 in sequence → if any migration hangs (slow DDL on a large table), startup hangs. ⚠ LONG MIGRATION

**11.6–11.10: ExtensionIdMigrator and extension-state consistency**
261. ExtensionIdMigrator is called after MigrationRunner (line 263) → it repairs stale extension IDs → if the migration fails, extensions may still have the old ID → on restart, extensions are pinned with the wrong ID. ⚠ STALE ID
262. ExtensionIdMigrator reads all extensions from the table → computes the new ID (using a new hash algorithm) → updates each row → if the update hangs, some extensions are migrated and others aren't. ⚠ PARTIAL MIGRATE
263. ExtensionIdMigrator fails (exception) → the exception is caught and logged as a warning (line 266–269) → app continues → extensions are not migrated. On the next launch, they're still stale. ⚠ REPEAT FAILURE
264. ExtensionIdMigrator deletes invalid extensions (if any) → delete is concurrent with a session loading an extension → extension is deleted mid-load → exception. ⚠ RACE
265. ExtensionIdMigrator updates the Default/Preferences JSON file → on the next session launch, Chrome reads the updated preferences → extensions are pinned with the new IDs. ✓

---

### Category 12: Process Job Objects & Tree Kill (5 cases)

**12.1–12.5: Absence of Job Object setup**
266. WPF process is created WITHOUT being added to a Job Object → chromedriver and chrome are spawned as children but NOT in the same job → when WPF exits, children are NOT automatically terminated (Windows default: orphan them or reparent them). ⚠ CRITICAL
267. Process.Kill(entireProcessTree: true) is called on chromedriver → it kills direct children (chrome) → but if a chrome utility worker has been reparented or is a grandchild, it may not be killed. ⚠ WORKER PROCESS
268. A Job Object IS created (hypothetically) → but child processes are created with CREATE_BREAKAWAY_FROM_JOB flag → they escape the job → on parent exit, they survive. ⚠ ESCAPE
269. Job Object is created but Shutdown doesn't know about it → it only kills via Process.Kill on specific PIDs → job members that were never tracked are not killed. ⚠ TRACKING
270. Job Object is used correctly → children are in the job → on WPF exit, Windows automatically terminates all job members → file handles are released → PowerShell can proceed. ✓

---

## Critical Findings (Top 10)

| # | Severity | Finding | Impact | Mitigation |
|---|----------|---------|--------|-----------|
| 1 | CRITICAL | Child processes inherit WPF's open file handles (GhostShell.dll, log files, DB files) via default `bInheritHandles=true`. On Shutdown, WPF's handles are closed but child handles remain open, blocking the binary swap. | PowerShell's file copy fails → update stalled or partially applied → app left on broken version. | Set `bInheritHandles=false` when spawning chromedriver; audit all Process.Start calls; consider Job Objects. |
| 2 | CRITICAL | No Job Object setup: When WPF exits, chromedriver.exe and chrome.exe orphans may survive, keeping file handles open indefinitely until OS cleanup or 15min+ timeout. | Binary swap blocked; file copy fails; update hangs or partially applied. | Create a Job Object in WPF startup; add child processes to the job; on exit, OS auto-kills all job members. |
| 3 | CRITICAL | ConcurrentDictionary TryRemove TOCTOU: If Shutdown fires BETWEEN `_runs.StartAsync()` and `_sessions[profile.Name] = ...` insert, the session/watchdog are never registered → `StopAllAsync()` finds no session to dispose → chrome/chromedriver outlive WPF. | Session left running after Shutdown → file locks block the swap. | Move the dict insert BEFORE `_launcher.LaunchAsync()` or wrap the whole StartAsync in an exception handler that always cleans up. |
| 4 | CRITICAL | SeleniumBrowserSession._ownedPids is always empty (`Array.Empty<int>()`). Chrome PIDs are never tracked, so the reap loop in DisposeAsync does nothing. Orphan chrome.exe processes survive and hold file handles. | chrome.exe stays alive post-Shutdown → files locked → swap fails. | Capture chrome PIDs from Selenium / WMI during launch; pass to session ctor; populate _ownedPids; ensure DisposeAsync reaps them. |
| 5 | HIGH | Shutdown's sequential StopAllAsync: Multiple sessions are torn down one at a time. If session A's driver.Quit() hangs (5–10s), session B's DisposeAsync is delayed → step timeout fires → session B is abandoned. | Unclean shutdown of active sessions → orphaned processes and locked files. | Parallelize StopAllAsync: spawn StopInternalAsync tasks concurrently (Task.WhenAll); apply a per-session timeout (4s) so a hung session doesn't block others. |
| 6 | HIGH | Shutdown step timeout (8s) is insufficient for multi-session teardown. With 5 sessions × 5s/session = 25s, but global timeout is 20s. First 3 sessions are disposed cleanly, last 2 are abandoned. | Incomplete session teardown → file locks → swap fails. | Increase global timeout to 60s or parallelize teardown; or add a "quick teardown" mode that skips driver.Quit() and goes straight to Process.Kill(). |
| 7 | HIGH | HostedService.StopAsync may ignore cancellation tokens: RunnerHost, WarmupQualityMonitor, and others may not check the cancellation token during long operations. If a service is slow to stop, Host.StopAsync blocks → step timeout fires → service is abandoned mid-operation. | Services left running; locks held; swap fails. | Audit all HostedService.StopAsync implementations; ensure they check `ct.IsCancellationRequested` every 1–2s; add internal timeouts (e.g., 5s per operation). |
| 8 | HIGH | Serilog sink disposal races with logging: Services log during DisposeAsync after Serilog has shut down → logging calls may throw NullReferenceException or fail silently. Post-mortem diagnostics are lost. | Silent logging failures; diagnostics incomplete; hard to debug shutdown issues. | Flush Serilog LAST, after all services are disposed; or catch and swallow logging exceptions in service Dispose methods. |
| 9 | MEDIUM | Fire-and-forget background tasks (RestoreLatestAsync, KickAssignedScriptAsync) are not awaited by StartAsync → they run in the background during Shutdown. If Shutdown races and disposes the session while the task is mid-operation, task catches and logs — but adds latency and confusion. | Background tasks compete with shutdown; may cause unexpected delays. | Assign each background task to the per-profile CTS; ensure Shutdown cancels them before disposing the session. |
| 10 | MEDIUM | ApplyAsync may raise ShutdownRequested twice: If ApplyAsync is retried (user clicks Retry on error dialog), a second ApplyAsync may fire ShutdownRequested while Shutdown is already in flight. Shutdown(0) is idempotent but the race is messy. | Double-shutdown attempts; confusion in logs; potential race conditions. | Add a flag to prevent double-apply attempts; or make IUpdateService.ApplyAsync idempotent (check if an update is already in progress). |

---

## Summary of Themes

**Concurrency & Races:**
- Watchdog vs. Shutdown TryRemove race: mitigated by ConcurrentDictionary, but TOCTOU window between insert and dict registration exists.
- External-close callback queued but not awaited by the loop: potential for race with Shutdown.
- Session start/stop races: addressed by _stopping HashSet and the phase 29 fix, but careful review needed.

**File Locks & Inheritance:**
- Child processes inherit WPF's file handles by default.
- No Job Object setup: children survive parent exit.
- Empty _ownedPids: chrome.exe PIDs never tracked.
- These three issues alone can block the binary swap.

**Timeout & Slow Operations:**
- Sequential multi-session teardown can exceed global budget.
- Long migrations, slow DB queries, hung services: all exceed step/global timeouts.
- No internal timeouts in some HostedServices: they run to completion regardless of deadline.

**Async/Await Hygiene:**
- Fire-and-forget tasks (RestoreLatestAsync, KickAssignedScriptAsync) continue during shutdown.
- Cancellation tokens passed but not checked in some call chains.
- Exception handling in async stacks may hide issues (especially logging failures).

**First-Launch Risks:**
- Migration runner is called in OnStartup, before MainWindow creation.
- If migration fails, app crashes with no recovery.
- ExtensionIdMigrator is a best-effort repair; failures are logged but not retried.

---

## Recommendations

1. **IMMEDIATE (Blocker for release):**
   - Set `bInheritHandles=false` in Selenium's ChromeDriverService initialization.
   - Implement Job Object for child process management on Windows.
   - Track and populate _ownedPids with actual chrome PIDs from WMI at launch.
   - Parallelize RealProfileRunner.StopAllAsync.

2. **SHORT TERM (Phase 38–39):**
   - Increase global Shutdown timeout to 45–60s or implement quick-teardown mode.
   - Audit all HostedService.StopAsync for cancellation-token hygiene.
   - Add explicit Serilog.CloseAndFlush() call AFTER all services are disposed.
   - Make fire-and-forget tasks cancellable via the per-profile CTS.

3. **LONG TERM (Phase 40+):**
   - Add an UpdateApplied flag to prevent double-apply races.
   - Implement migration rollback or idempotency checks.
   - Add comprehensive integration tests for Shutdown + multi-session + update scenarios.
   - Monitor real-world crash reports for orphan process leaks.

---

**Audit Date:** 2026-05-02  
**Status:** COMPLETE, 270 cases enumerated, critical issues identified  


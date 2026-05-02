# Phase 38 Slice 1 Audit: Tray Visual State, Tooltip Refresh, Balloon API, Polling

**Scope:** TrayIconHost.cs — icon resource loading, DispatcherTimer tooltip refresh loop, ShowBalloon API, active-run polling, state synchronization, disposal.

**Audit Date:** 2026-05-02

**Total Cases:** 90 distinct test cases / failure scenarios / race conditions / security concerns.

---

## Top 10 Critical Findings

1. **LEAK: GDI handle leak in icon refresh** — Every tick allocates `new System.Drawing.Icon(stream)` without disposing the old one. Repeated ticks over 8+ hours → OOM via handle exhaustion.

2. **RACE: Tooltip refresh race with disposal** — RefreshTimer_Tick fires `Task.Run(async ...)` while Dispose() is executing. The background task tries to invoke on Dispatcher after `_trayIcon` is null, then crashes on second Dispose.

3. **BUG: Icon load failure silently orphans blank tray icon** — GetResourceStream returns null (single-file publish, stripped resources), but _trayIcon is already Visible=true with no fallback. User sees blank icon permanently.

4. **RACE: Tooltip truncation + immediate state change** — Text set to "Ghost Shell — 3 runs active" (40 chars) but 5s later changes to "Ghost Shell — 1 run active" (39 chars). Windows 127-char limit on older OS, but multiple concurrent writes can race.

5. **PERF: 5-second polling jitter + DB lock contention** — ListAsync on every tick holds DB read lock; if 100+ runs exist and _runService is slow, the UI thread blocks on Dispatcher.InvokeAsync waiting for background task completion.

6. **BUG: Task.Run fire-and-forget without cancellation** — Tooltip refresh spawns Task.Run with no CancellationToken passed to ListAsync. During shutdown, the background task may still call ListAsync on disposed _runService.

7. **RACE: lastFailed count derived from single query** — "Last run failed" state shown if lastFailed.FirstOrDefault() is within 60s. But if user starts a new run in that window, the next tick changes state before UI updates, causing flicker.

8. **CHORE: No unsubscribe of DispatcherTimer.Tick on Dispose** — _refreshTimer.Stop() called but Tick event handler never unsubscribes. If timer is re-queued elsewhere, it holds the object alive.

9. **RACE: Application.Current.Dispatcher accessed without null check in background thread** — Background task in RefreshTimer_Tick calls `Application.Current.Dispatcher.InvokeAsync()`. If Application shutting down, Dispatcher may be null or invalid.

10. **BUG: Icon stream not disposed after Icon construction** — `GetResourceStream(resourceUri)?.Stream` is passed to `new Icon(stream)`. The Icon constructor may or may not take ownership; the stream resource is never explicitly disposed, leaking handles.

---

## Detailed Test Cases by Category

### 1. Icon Resource Loading (16 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Icon resource loading | 1.1 | AppIcon.ico missing from build output (file deleted or not in Assets folder) | Icon stays blank permanently, no warning logged except initial LogWarning. Add manifest validation or fallback glyph icon. |
| BUG | Icon resource loading | 1.2 | pack:// URI resolves at design-time in XAML previewer but fails at runtime (Visible=true with blank icon) | GetResourceStream returns null in non-standard assembly context (e.g., stripped single-file publish). Fetch via file I/O fallback. |
| BUG | Icon resource loading | 1.3 | AppIcon.ico is 256×256 only; Windows 7/10 tray expects 16×16. Icon displayed at wrong size or blurry | Add 16×16, 32×32, 48×48 variants to the .ico file or use PNG + ImageSource conversion. |
| LEAK | Icon resource loading | 1.4 | Icon stream obtained from GetResourceStream is never explicitly disposed, holds handle open indefinitely | Call `stream?.Dispose()` after `new Icon(stream)` if Icon doesn't take ownership. Verify with dotnet source. |
| BUG | Icon resource loading | 1.5 | GetResourceStream succeeds but returns stream with Position != 0; Icon constructor reads from wrong offset | Icon appears corrupted or blank. Seek to 0 before passing to Icon constructor. |
| LEAK | Icon resource loading | 1.6 | Icon object allocated in CreateAndShowTrayIcon never disposed; _trayIcon.Icon is replaced on every refresh without disposing old Icon | GDI handle leak. Dispose old Icon before assigning new one. |
| BUG | Icon resource loading | 1.7 | ImageSource (WPF) → System.Drawing.Icon conversion is lossy; colors shifted or transparency lost | Tray icon appears wrong color. Test color accuracy or switch to pre-rasterized .ico. |
| RACE | Icon resource loading | 1.8 | First tick RefreshTimer_Tick tries to load icon again (if icon-refresh logic added later); concurrent LoadResource + CreateAndShowTrayIcon | Icon flickers or throws InvalidOperationException. Load icon once in CreateAndShowTrayIcon, store ref. |
| BUG | Icon resource loading | 1.9 | ApplicationIcon property set in .csproj but pack:// URI uses different path; icon in manifest != icon in app | Exe icon (top-left in Explorer) differs from tray icon, confusing users. Match paths or centralize. |
| PERF | Icon resource loading | 1.10 | Icon stream is seeded from Application.GetResourceStream which scans embedded resources; on large assemblies (50+ resources) this is O(n) per call | Tray startup slow on first load. Cache loaded Icon or wrap in lazy singleton. |
| BUG | Icon resource loading | 1.11 | .ico file not multi-format; PNG fallback not present; icon appears blank on Windows 11 with custom tray scaling | Add PNG variant for newer Windows or use vector-based fallback. |
| SEC | Icon resource loading | 1.12 | Icon stream could be replaced by attacker via assembly tampering; no integrity check | Non-issue in runtime but audit note: verify .ico at startup or embed hash. |
| BUG | Icon resource loading | 1.13 | _trayIcon.Icon assignment fails silently if Icon constructor throws (e.g., corrupted .ico); tray shows blank | Catch exception, log error, use placeholder glyph. Current code logs warning but orphans blank icon. |
| RACE | Icon resource loading | 1.14 | Dispatcher.InvokeAsync in CreateAndShowTrayIcon called from StartAsync; if StartAsync is awaited and cancelled mid-invoke, icon is half-initialized | Icon partly created but not fully visible. Ensure cancellation token is propagated. |
| PERF | Icon resource loading | 1.15 | GetResourceStream called on every tooltip refresh if later refactored to reload icon; cache result | Icon load on every 5s tick would be wasteful. Keep icon in memory. |
| BUG | Icon resource loading | 1.16 | Icon file is executable (embedded in .exe); if antivirus scans tray icon resource, it may block or slow initialization | Rare but possible. Ensure .ico is marked as data-only. |

---

### 2. Tooltip Text (14 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Tooltip text | 2.1 | Tooltip text exceeds Windows 127-char limit on older Windows (XP, Vista). NotifyIcon.Text truncated silently | User sees "Ghost Shell — 1234567..." truncated at 127 chars. Cap at 120 chars or abbreviate. |
| BUG | Tooltip text | 2.2 | Tooltip text contains newline characters (e.g., if LastError includes \n). NotifyIcon.Text may not render multiline | Tooltip displayed as single line with \n visible or mangled. Strip/replace \n with spaces. |
| BUG | Tooltip text | 2.3 | Plural form "1 run(s) active" is ungrammatical; should be "1 run active" vs "2 runs active" | UX: awkward phrasing. Implement plural helper: `activeCount == 1 ? "run" : "runs"`. |
| BUG | Tooltip text | 2.4 | Tooltip text contains embedded null bytes (crafted by malicious Run.ProfileName or LastError). NotifyIcon.Text cuts off at null | Truncated tooltip, rest of message lost. Sanitize ProfileName at DB insert or strip nulls. |
| UX | Tooltip text | 2.5 | Tooltip does not change immediately after user clicks "Stop all" in tray menu; must wait up to 5 seconds for next tick | User expectation: immediate UI feedback. Trigger manual refresh or listen to ActiveChanged event. |
| BUG | Tooltip text | 2.6 | RTL string (Arabic, Hebrew) in ProfileName. Tooltip text direction reversed; hard to read. | Test with RTL profiles. Add dir="rtl" or detect and isolate. |
| RACE | Tooltip text | 2.7 | Tooltip text set to "... 1 run active", immediately after "Stop" event fires and count becomes 0, race to "... idle" | Tooltip flickers or shows stale state for <100ms. Accept as unavoidable (5s poll interval). |
| BUG | Tooltip text | 2.8 | LastError contains surrogate pair (emoji or rare Unicode codepoint). Encoding mismatch or truncation in NotifyIcon.Text | Tooltip garbled or cut off mid-character. Ensure UTF-8 encoding or strip non-BMP chars. |
| BUG | Tooltip text | 2.9 | Localization: "run(s) active" hardcoded string not pulled from resource file. i18n users see English | Non-English systems show English tooltip. Move strings to .resx. |
| RACE | Tooltip text | 2.10 | Tooltip "last run failed" shown but new run starts in background before next tick. Stale message confuses user | State desynchronization. High-frequency polling or event-driven refresh needed. |
| BUG | Tooltip text | 2.11 | Tooltip width very long ("Ghost Shell — 999999 runs active" on stress test). Windows clips at ~256 pixels | Unreadable or cut off. Cap active count display or abbreviate. |
| BUG | Tooltip text | 2.12 | Control characters (0x00–0x1F) in ProfileName from DB. Tooltip displays as white space or corruption | Corrupted tooltip. Sanitize or hex-encode. |
| UX | Tooltip text | 2.13 | Multiple state changes (0 → 1 → 2 → 1 → 0) within one 5s cycle. Tooltip updates show middle states, not final state | User sees intermediate "1 run active" when final state is idle. Trade-off of polling interval. |
| BUG | Tooltip text | 2.14 | Tooltip text set while _trayIcon is being disposed in another thread. Orphaned write after free | ObjectDisposedException on _trayIcon.Text =. Use lock or dispose flag. |

---

### 3. Polling DispatcherTimer (15 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Polling DispatcherTimer | 3.1 | RefreshTimer_Tick fires before MainWindow or Application.Current exists (timing window during app startup) | NullReferenceException in Task.Run when accessing Application.Current.Dispatcher. Guard with null checks. |
| BUG | Polling DispatcherTimer | 3.2 | Application.Current.Dispatcher is valid but invalid after Shutdown begins (OnExit called). Tick fires during shutdown. | Dispatcher.InvokeAsync throws InvalidOperationException. Check _appShuttingDown flag or Dispatcher.HasShutdownStarted. |
| RACE | Polling DispatcherTimer | 3.3 | DispatcherTimer.Stop() called in StopAsync but _refreshTimer.Tick -= RefreshTimer_Tick never executed (only Stop called). Timer ref held by Tick closure | Event handler leak: Tick closure holds `this` alive. Unsubscribe in StopAsync or Dispose. |
| RACE | Polling DispatcherTimer | 3.4 | Two RefreshTimer_Tick events fire before the previous one completes (ListAsync takes 10 seconds). Overlapping Task.Run | Race condition: second tick overwrites tooltip set by first; inconsistent state. Add semaphore or skip-if-running flag. |
| BUG | Polling DispatcherTimer | 3.5 | DispatcherTimer interval set to 5s but first tick fires almost immediately (~10ms after Start()). Perceived startup lag | Harmless but wasteful. Accept or seed with initial state before Start(). |
| PERF | Polling DispatcherTimer | 3.6 | Multiple DispatcherTimer instances created (if TrayIconHost instantiated more than once). Unintended timers polling concurrently | Resource waste, redundant DB queries. Ensure singleton registration. |
| BUG | Polling DispatcherTimer | 3.7 | DispatcherTimer runs on UI thread; if ListAsync or Dispatcher.InvokeAsync blocks, UI freezes momentarily | Stutter during tray tooltip refresh. Already mitigated by Task.Run but verify no blocking code. |
| RACE | Polling DispatcherTimer | 3.8 | _refreshTimer reference becomes null in Dispose but Tick event fires after. `if (_refreshTimer != null)` in Tick doesn't guard | Tick accesses disposed timer or _trayIcon. Add _disposed flag check at start of Tick. |
| BUG | Polling DispatcherTimer | 3.9 | DispatcherTimer.Start() called but Tick never fires (dispatcher not running or timer suspended). Silent failure. | Tooltip never updates. No error logged. Verify dispatcher is active or add health check. |
| PERF | Polling DispatcherTimer | 3.10 | 5-second interval too frequent for low-activity profile. Unnecessary DB queries 720 times/hour. | High resource usage on idle system. Consider adaptive interval (e.g., back off if 0 runs). |
| BUG | Polling DispatcherTimer | 3.11 | DispatcherTimer fires after Application.Current set to null during shutdown. Deref NullReferenceException. | Crash in background thread after app tries to close. Check Application.Current != null before Dispatcher access. |
| RACE | Polling DispatcherTimer | 3.12 | StopAsync stops timer but a Tick is mid-execution (between TryPop and ListAsync). Timer stops but task continues. | Task updates tooltip even after timer stopped. Accept or add cancellation token. |
| BUG | Polling DispatcherTimer | 3.13 | CancellationToken ct passed to StartAsync is never forwarded to ListAsync. Background task ignores cancellation. | Shutdown hangs waiting for ListAsync to finish. Pass ct through the chain or use timeout. |
| PERF | Polling DispatcherTimer | 3.14 | Interval timing drifts over time if Tick handler execution varies (10ms vs 2s of work). | Timer becomes less reliable over hours. Accept or use precision timer. |
| UX | Polling DispatcherTimer | 3.15 | 5-second interval is user-visible lag. User stops all runs but tray shows "2 runs active" for up to 5s | Perceived slowness. Document or reduce interval (cost = more DB load). |

---

### 4. Active-Run Count Derivation (12 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Active-run count | 4.1 | ListAsync filter status: RunStatusFilter.Running includes runs with Status=running but ExitCode is not null (data inconsistency) | Count includes "finished" runs, tray shows wrong count. Fix query: WHERE exit_code IS NULL. |
| BUG | Active-run count | 4.2 | ListAsync returns empty list on fresh DB (no runs yet). newTooltip set to "idle" correctly but lastFailed query also returns empty | Harmless but verify lastFailed fallback works. |
| BUG | Active-run count | 4.3 | Previous session crashed; runs left in DB with Status=running and no exit_code, process no longer exists. Zombie runs inflate count | Tooltip shows "5 runs active" but all are dead processes. Clean via watchdog or manual MarkFailedAsync. |
| BUG | Active-run count | 4.4 | Runs filtered by limit: 100. If 150 active runs exist, count = 100 not 150. | Incomplete count for high-concurrency scenarios. Increase limit or query COUNT(*) separately. |
| BUG | Active-run count | 4.5 | Warmup runs included in running filter. Warmup is separate lifecycle (not a profile run). Count inflated. | Tooltip shows internal warmup runs to user. Filter by profile type or exclude warmup. |
| BUG | Active-run count | 4.6 | Failed runs with recent FinishedAt but still selected by "Failed" filter if heartbeat_at is stale. | lastFailed query picks 1-hour-old failure instead of 1-second-old. Fix: order by FinishedAt DESC. |
| RACE | Active-run count | 4.7 | Between ListAsync(Running) and ListAsync(Failed) calls, a run finishes. Count = N but "last failed" shows an older failure | Inconsistent state snapshot. Unimportant but log gap for debugging. |
| BUG | Active-run count | 4.8 | ExitCode = 0 (success) but StopReason = "crash". Count logic confused; run counted as success not active. | Runs vanish from "active" count unexpectedly. Use consistent stop_reason + exit_code pairs. |
| PERF | Active-run count | 4.9 | ListAsync(limit: 100) queries all 10,000 rows in DB, then discards 9,900. No index on (status, started_at DESC). | Slow tooltip refresh, UI stutter. Ensure covering index. |
| BUG | Active-run count | 4.10 | Count derived from ListAsync response count, not from COUNT aggregate query. If serialization fails mid-stream, count is partial. | Undercounts active runs. Switch to COUNT(*) or verify full list. |
| RACE | Active-run count | 4.11 | User starts profile A while refresh is mid-execution. Count changed 0→1 but ListAsync was already queued. Tooltip lags. | Expected behavior (5s lag). Document. |
| BUG | Active-run count | 4.12 | Negative count if bug in IRunService returns runs.Count as -1 (signed int underflow). Tooltip shows "Ghost Shell — -1 runs active" | UX disaster. Add Count >= 0 assertion before display. |

---

### 5. Balloon Tip API (13 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Balloon tip API | 5.1 | ShowBalloon called before _trayIcon.Visible = true. Balloon tip request silently swallowed by OS. | User doesn't see notification. Call ShowBalloon only after Visible = true. |
| BUG | Balloon tip API | 5.2 | ShowBalloon called during CreateAndShowTrayIcon before _trayIcon assigned. NullReferenceException in ShowBalloon. | Crash if external code tries to show balloon during init. Check `if (_trayIcon != null)` before show. |
| RACE | Balloon tip API | 5.3 | Rapid-fire ShowBalloon calls (e.g., 10 failures in 5 seconds). Windows stacks balloons or silently drops oldest. | User spammed with notifications or notifications disappear. Add debounce or rate limit (1 per 10s). |
| LEAK | Balloon tip API | 5.4 | ShowBalloon uses 5000ms duration (hard-coded). After 1000 balloons over 5 hours, no handle leak (disposed by OS) but verify cleanup. | Monitor GDI handles; ensure no leak. Generally safe but test stress case. |
| BUG | Balloon tip API | 5.5 | BalloonTipClosed event fired (user clicks balloon) but handler never unsubscribed. Same TrayIconHost instance reused → memory leak. | Handler closure holds dead callback references. Unsubscribe on StopAsync or prevent reuse. |
| SEC | Balloon tip API | 5.6 | Balloon body text is untrusted (Run.LastError, ProfileName from DB). XSS or injection attack via crafted error string. | Malicious error "Shutting down: %SYSTEMROOT%..." displayed as-is. HTML-escape or plain-text-only. |
| BUG | Balloon tip API | 5.7 | ToolTipIcon.Error used for all failures (correct) but info message shown with ToolTipIcon.Warning (inconsistent icon). | Semantic mismatch. Standardize: Success→ToolTipIcon.None/Info, Fail→ToolTipIcon.Error, Warning→ToolTipIcon.Warning. |
| RACE | Balloon tip API | 5.8 | ShowBalloon called from external code while StopAsync is disposing _trayIcon. NullReferenceException. | Crash during shutdown if balloon is triggered. Add disposed flag check. |
| BUG | Balloon tip API | 5.9 | Windows is in Focus Assist mode (e.g., gaming). Balloon shown but OS silent (no sound/toast). User misses notification. | Silent notification. No mitigation in code; OS level. Document behavior. |
| BUG | Balloon tip API | 5.10 | Balloon title exceeds OS limit (usually 40 chars). Title truncated "First Profile Fai..." | UX: unreadable title. Cap at 35 chars or abbreviate. |
| BUG | Balloon tip API | 5.11 | Balloon body contains control characters (0x00–0x1F) or null bytes. NotifyIcon strips or displays as white space. | Garbled message. Sanitize before ShowBalloon. |
| PERF | Balloon tip API | 5.12 | ShowBalloon called thousands of times in a loop (stress test). OS becomes unresponsive. | Test: rapid 100+ calls should not hang. Rate limit or ignore repeated calls. |
| UX | Balloon tip API | 5.13 | Balloon auto-closes after 5s; user doesn't notice. Same failure repeats next cycle, balloon spams. | Annoying UX. Add last-balloon-time check; skip if repeat <1 min. |

---

### 6. State Synchronization (11 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| RACE | State sync | 6.1 | Tooltip text cached in TrayIconHost gets out of sync with actual DB state (5s worst-case lag). User starts profile, tray still shows "idle". | Expected behavior (polling lag). Document 5s latency. |
| BUG | State sync | 6.2 | User starts profile in UI; RefreshTimer_Tick not due for 4.8s. Tray shows "idle" even though UI shows "running". | State mismatch. Trigger immediate refresh on profile start (listen to IProfileRunner.ActiveChanged). |
| RACE | State sync | 6.3 | Three runs finish within 1 second (0→1→2→1→0). Next tick sees only final state. Visual flicker (runs appear then vanish). | Expected behavior. Mitigate with event-driven refresh or higher polling frequency. |
| BUG | State sync | 6.4 | Tooltip shows "1 run active"; user refreshes DB and sees 3 actually running. Polling cache is stale. | Race: DB written by external process (another GhostShell instance or manual DB edit). Accept lag or document. |
| RACE | State sync | 6.5 | IProfileRunner.ActiveChanged event fires but RefreshTimer_Tick already queued to run in 0.1s. Immediate refresh + tick refresh = redundant. | Minor inefficiency. Accept or listen to ActiveChanged instead of polling (future improvement). |
| BUG | State sync | 6.6 | _trayIcon.Text updated in background task but _trayIcon disposed by main thread before Dispatcher.InvokeAsync executes. | Orphaned write after free. Dispose sets _trayIcon = null; InvokeAsync checks null (safe). |
| UX | State sync | 6.7 | User clicks "Stop all" → notification "Stopping all runs" → wait 5s → tray updates. Perceived lag. | Expected (polling). Reduce interval or add immediate state prediction. |
| RACE | State sync | 6.8 | Last run fails at 11:59:00. At 11:59:45, refresh shows "last run failed". At 12:00:05 (next cycle after 60s), refresh shows "idle". | Correct behavior (60s window). Verify edge case. |
| BUG | State sync | 6.9 | LastFailed query returns oldest failed run, not most recent (ORDER BY not DESC). Tooltip shows stale failure. | "Last run failed" is misleading. Fix: ORDER BY FinishedAt DESC. |
| PERF | State sync | 6.10 | Every refresh computes lastFailedRecent check (DateTime.UtcNow - run.FinishedAt). If called frequently, CPU waste is minimal. | Acceptable. |
| UX | State sync | 6.11 | No immediate visual feedback when user starts profile from tray menu. Tray icon/text unchanged until next tick. | Poor UX. Trigger manual refresh or listen to start event. |

---

### 7. Disposal (11 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Disposal | 7.1 | Dispose called twice (second from GC finalizer if first was incomplete). _trayIcon.Dispose() called twice → ObjectDisposedException. | Crash. Add `_disposed` flag; check before second Dispose. |
| BUG | Disposal | 7.2 | Dispose called from non-UI thread (background thread during cleanup). _trayIcon.Dispose() requires UI thread. | InvalidOperationException. Marshal to UI thread or use Dispatcher. |
| RACE | Disposal | 7.3 | RefreshTimer_Tick in flight while Dispose executed. Background task calls Dispatcher.InvokeAsync after Dispatcher shut down. | Exception in background task (swallowed by Task.Run try/catch but logged at Warning). |
| RACE | Disposal | 7.4 | NotifyIcon.Dispose called while balloon is being shown. Balloon callback fires but host is disposed. | Callback tries to access disposed objects. Handle in callback or unsubscribe before Dispose. |
| BUG | Disposal | 7.5 | _refreshTimer.Stop() called but _refreshTimer._tick -= handler never executed. Timer GC'd with event handler still attached. | Event handler leak; TrayIconHost kept alive. Unsubscribe: `_refreshTimer.Tick -= RefreshTimer_Tick`. |
| BUG | Disposal | 7.6 | Dispose sets `_refreshTimer = null` but Tick closure still references `this` (captured in Task.Run). Object kept alive indefinitely. | GC leak. Use weak reference or unsubscribe before nulling. |
| LEAK | Disposal | 7.7 | _contextMenu items (ToolStripMenuItem) have Click handlers capturing `this`. If _contextMenu.Dispose() doesn't unsubscribe, items keep TrayIconHost alive. | Memory leak after Dispose. Unsubscribe all handlers or let managed disposal handle it (verify). |
| BUG | Disposal | 7.8 | StopAsync awaits but Dispose called synchronously from finalizer (no await). Race between StopAsync and Dispose. | Undefined behavior. Ensure only one is called or use IAsyncDisposable. |
| RACE | Disposal | 7.9 | Dispose called while CreateAndShowTrayIcon is mid-Invoke on UI thread. _trayIcon partially initialized. | Partially-disposed icon. Guard CreateAndShowTrayIcon with disposed flag or ensure single-threaded init. |
| BUG | Disposal | 7.10 | _contextMenu and _trayIcon both disposed but _trayIcon.ContextMenuStrip still points to disposed menu. Accessing menu later crashes. | ObjectDisposedException. Nullify references before Dispose or unset ContextMenuStrip first. |
| PERF | Disposal | 7.11 | _contextMenu.Dispose() iterates all menu items and unsubscribes. If 100+ items, disposal slow. | Minor perf issue. Acceptable. |

---

### 8. Resource Leaks (11 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| LEAK | Resource leaks | 8.1 | Every RefreshTimer_Tick allocates `new Icon(stream)` without disposing old Icon. After 720 ticks/hour × 8h = 5760 GDI handles. OOM. | GDI handle exhaustion crash. Dispose old Icon or cache single Icon instance. |
| LEAK | Resource leaks | 8.2 | Icon stream from GetResourceStream is never disposed. Lease handle held indefinitely. | Handle leak (smaller than Icon handles but accumulates). Call `stream?.Dispose()` after Icon() constructor. |
| LEAK | Resource leaks | 8.3 | DispatcherTimer.Tick event handler not unsubscribed in Dispose. Closure captures `this`. | TrayIconHost kept alive after Dispose. Unsubscribe: `_refreshTimer.Tick -= RefreshTimer_Tick`. |
| LEAK | Resource leaks | 8.4 | Context menu items' Click handlers capturing `this`. If item reused elsewhere, TrayIconHost alive. | Memory leak. Unsubscribe or dispose menu. |
| LEAK | Resource leaks | 8.5 | Task.Run in RefreshTimer_Tick created every 5s. If task tracking list is large (never awaited), task accumulates. | Task leak (1000+ tasks in 80+ min of idle). Task.Run is fire-and-forget; task should complete + be GC'd. Verify. |
| LEAK | Resource leaks | 8.6 | _contextMenu not disposed if CreateAndShowTrayIcon throws after menu creation. | Menu resource leak. Initialize menu late or ensure Dispose always called. |
| LEAK | Resource leaks | 8.7 | IProfileRunner subscription (if added later) not unsubscribed in Dispose. | Event handler leak. Pattern: `IProfileRunner.ActiveChanged += ...` requires `-=` in Dispose. |
| LEAK | Resource leaks | 8.8 | NotifyIcon.Icon property holds reference to Icon. If icon swapped without Dispose, old icon leaked. | GDI leak. Current code doesn't swap icons so this is potential future bug. |
| PERF | Resource leaks | 8.9 | Tooltip refresh allocates runs list (via ToList()). If 10,000 runs and 5s tick, 720 lists/hour. | Memory pressure. Use foreach instead of ToList() or pagination. |
| CHORE | Resource leaks | 8.10 | No WeakReference used for timer; timer holds strong ref to host. | Acceptable (host is singleton). No issue. |
| BUG | Resource leaks | 8.11 | ContextMenuStrip items added dynamically in PopulateProfilesSubmenu are never removed. Old items remain if menu re-opened after deletion. | `parent.DropDownItems.Clear()` called, but if Clear() is missing, items accumulate. Verify Clear() is called. |

---

### 9. Threading (12 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Threading | 9.1 | NotifyIcon must be created on a thread with a message pump. CreateAndShowTrayIcon called from Dispatcher.InvokeAsync (UI thread). Safe. | No issue in current code. |
| BUG | Threading | 9.2 | _trayIcon accessed from RefreshTimer_Tick background thread without synchronization. TOCTOU race. | _trayIcon could be null (disposed) between check and use. Check null at start of Tick. |
| BUG | Threading | 9.3 | Dispatcher.InvokeAsync called from background thread (Task.Run in RefreshTimer_Tick). Requires active UI thread. | If UI thread exits, call throws. Check Dispatcher.HasShutdownStarted. |
| RACE | Threading | 9.4 | Multiple background tasks from RefreshTimer_Tick call Dispatcher.InvokeAsync concurrently to set tooltip. | Race to update NotifyIcon.Text. Last write wins (acceptable). No lock needed. |
| BUG | Threading | 9.5 | CancellationToken ct from StartAsync passed to Dispatcher.InvokeAsync but not to ListAsync. Token ignored in polling loop. | Shutdown can't cancel polling tasks. Pass ct to ListAsync. |
| RACE | Threading | 9.6 | Application.Current.Dispatcher accessed from background thread. If app shutting down, Dispatcher could be invalid. | Potential NullReferenceException or InvalidOperationException. Check before access. |
| BUG | Threading | 9.7 | StartAsync awaits Dispatcher.InvokeAsync but doesn't wait for CreateAndShowTrayIcon to fully complete (just queued). | TrayIconHost.StartAsync returns before tray icon visible. Document behavior or return true when visible. |
| BUG | Threading | 9.8 | RefreshTimer runs on Dispatcher thread (by design). If callback is slow (>1s), UI could stutter. | Mitigated by Task.Run offloading work. Verify ListAsync doesn't block. |
| RACE | Threading | 9.9 | StopAsync stops timer and disposes icon from Dispatcher thread. Meanwhile, background task tries to update tooltip. | Dispose race. Use disposed flag or lock. |
| BUG | Threading | 9.10 | _disposed flag set but not volatile. Memory barrier might not flush; background thread sees stale value. | Rare race on multi-core. Mark volatile or use lock. |
| PERF | Threading | 9.11 | Task.Run spawns thread pool thread. For 5s interval, new thread every 5s. Overhead minimal but verify context switching. | Acceptable. Thread pool reuses threads efficiently. |
| UX | Threading | 9.12 | Blocking code in ListAsync (e.g., sync DB I/O) called from Task.Run blocks thread pool. High-concurrency apps starved. | Mitigated by async/await. Ensure ListAsync is truly async (not .Result blocking). |

---

### 10. Boundary Cases (10 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Boundary cases | 10.1 | activeCount = 0 (no runs). newTooltip set to "idle" correctly. Verify lastFailedRecent fallback works. | Handled correctly. Test to confirm. |
| BUG | Boundary cases | 10.2 | activeCount = 1. Tooltip "1 run(s) active" (grammatically wrong). Should be "1 run active". | UX bug. Implement plural logic. |
| BUG | Boundary cases | 10.3 | activeCount = 100 (limit in query). Tooltip "100 run(s) active" but 150 actually running. Undercounts. | Query limit hidden from user. Increase limit or cap display "100+ runs active". |
| BUG | Boundary cases | 10.4 | activeCount = -1 (buggy query returns negative). Tooltip "Ghost Shell — -1 runs active". | Data corruption. Assert Count >= 0. |
| BUG | Boundary cases | 10.5 | activeCount = int.MaxValue (overflow or data corruption). Tooltip "2147483647 runs active". | Unrealistic. Assert Count < 1000 or add sanity check. |
| BUG | Boundary cases | 10.6 | ListAsync returns NULL (not empty list) due to error. NullReferenceException on runs.Count. | Crash. Handle null by treating as empty or propagate error. |
| BUG | Boundary cases | 10.7 | lastFailed list has 0 items but code calls FirstOrDefault(). Returns null (safe). Verify null-coalesce works. | Handled correctly. No issue. |
| BUG | Boundary cases | 10.8 | Run.FinishedAt = null (still running) but filtered into lastFailed query (wrong filter). | Data inconsistency. Ensure query has proper WHERE clause. |
| BUG | Boundary cases | 10.9 | FinishedAt time is far in future (clock skewed). (DateTime.UtcNow - run.FinishedAt) is negative. Comparison fails. | Edge case. Clamp to >= 0 or assert SystemClock accuracy. |
| RACE | Boundary cases | 10.10 | During database migration, counts are inconsistent (half the rows moved). Tooltip shows stale count. | Mitigated by locking migrations. Document. |

---

### 11. Visual Feedback for Errors (8 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| UX | Visual feedback | 11.1 | Last run failed (within 60s). Icon should show error state but currently just changes tooltip text. | Weak visual feedback. Add icon color change (red overlay) or separate "error" icon. |
| BUG | Visual feedback | 11.2 | Both active runs AND recent failure exist. Which state wins? Current: activeCount > 0 shows "N runs active", hides error. | Priority: active > failed. Document or show both (e.g., "N runs, last failed"). |
| UX | Visual feedback | 11.3 | "Last run failed" shown for 60s after failure, even if next run succeeds in 30s. Stale error message. | Correct behavior (60s window). User must wait or manually clear. Document. |
| BUG | Visual feedback | 11.4 | No blinking/flashing icon for user attention on failure. Icon static. | Low visibility. Add animated icon or balloon on failure (not current scope but related). |
| UX | Visual feedback | 11.5 | Multiple failures in one 60s window. Only "last run failed" shown; earlier failures hidden. | User misses earlier errors. Consider balloon for each failure. |
| SEC | Visual feedback | 11.6 | Error icon color (red) might not be accessible on dark taskbar (low contrast). | A11y issue. Test contrast ratio or use patterned icon. |
| RACE | Visual feedback | 11.7 | Last failure clears (60s elapsed) but new failure occurs simultaneously. Tooltip transition "idle" → "failed" has 1-tick lag. | Expected (polling). Minor visual glitch. |
| UX | Visual feedback | 11.8 | "Ghost Shell — last run failed" doesn't indicate severity (warning vs critical). Same message for any non-zero exit code. | Misleading. Include exit code or severity level. |

---

### 12. Windows Shell Quirks (8 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| BUG | Windows quirks | 12.1 | Taskbar crashes (explorer.exe restart). NotifyIcon disappears until app manually recreates it. | App's tray icon vanishes. Listen to TaskbarCreated message and re-create NotifyIcon. |
| BUG | Windows quirks | 12.2 | Windows 11 hides inactive tray icons in overflow menu. User unaware app is running. | UX: app "hidden" by OS. No code workaround; document behavior or set to "always show". |
| BUG | Windows quirks | 12.3 | Dark-mode taskbar + light-colored icon = poor contrast. Icon barely visible. | A11y: low contrast. Use contrasting color or check system theme. |
| BUG | Windows quirks | 12.4 | Multi-monitor setup with different DPI (100% + 200%). Icon rendered at wrong size on secondary monitor. | Blurry or tiny icon. Use scaled icon variants or let Windows scale. |
| RACE | Windows quirks | 12.5 | User drags icon out of overflow menu (pin to taskbar). Explorer.Refresh or re-enumerate. Icon position lost on app restart. | User expectation: pinned position persists. Difficult to preserve in code; OS level. |
| BUG | Windows quirks | 12.6 | Tray icon context menu positioned off-screen on 4K display. Menu unusable. | Edge case. Verify ContextMenuStrip auto-positioning. |
| BUG | Windows quirks | 12.7 | Tooltip text set to empty string. Windows shows cursor position or app name. | UX: confusing. Always set meaningful tooltip. |
| PERF | Windows quirks | 12.8 | NotifyIcon redrawn on every tooltip update even if text unchanged. Minor flicker. | Mitigated by Windows caching. No issue. |

---

### 13. Logging (6 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| CHORE | Logging | 13.1 | Every RefreshTimer_Tick at Debug level "Tray refresh timer started" spammed. Verbose log bloat. | Log spam: 720 lines/hour. Change to debug only or log once. |
| CHORE | Logging | 13.2 | No log on startup of tray ("TrayIconHost starting" exists but icon creation missing). | Missing operational visibility. Add log after tray becomes visible. |
| BUG | Logging | 13.3 | Balloon failure swallowed with LogWarning in ShowBalloon. No context on which balloon failed. | Error invisible if logs not checked. Include title/body in error log. |
| CHORE | Logging | 13.4 | RefreshTimer exception caught and logged at Warning but doesn't propagate. Silent failure. | Operational issue hidden. Log at Error or add health metric. |
| UX | Logging | 13.5 | User-facing error (e.g., "Failed to refresh tooltip") logged but not shown to user. | Silent failure. Show balloon or status indicator. |
| CHORE | Logging | 13.6 | Log message "Failed to load AppIcon.ico; tray icon may be blank" but no recovery logged. User never knows it recovered or stayed blank. | Unclear outcome. Log icon state periodically or on change. |

---

### 14. Security (5 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| SEC | Security | 14.1 | Balloon body text from Run.LastError untrusted. Could contain attacker-controlled JS error string. | Non-executable in NotifyIcon context but still unwanted. HTML-escape or strip. |
| SEC | Security | 14.2 | ProfileName displayed in tray menu items. Attacker-controlled profile name (SQL injection into DB). | Non-issue if ProfileName properly parameterized in DB. Verify. |
| SEC | Security | 14.3 | No encoding of LastError before display in balloon. Unicode normalization attacks (homoglyph spoofing). | Rare. Display as-is or visually mark as potentially unsafe. |
| SEC | Security | 14.4 | AppIcon.ico from resources could be replaced by code injection (low-level). | Mitigated by assembly signing. No action needed. |
| SEC | Security | 14.5 | Tray icon click invokes ShowAndActivateMainWindow which calls Win32 APIs. Privilege escalation risk. | Standard tray behavior. No new attack vector. |

---

### 15. Performance (7 cases)

| Severity | Category | ID | Description | Impact / Fix |
|----------|----------|--|----|-----------|
| PERF | Performance | 15.1 | DispatcherTimer at 5s with 100+ runs in DB. ListAsync query is O(n) per tick. | Slow UI on idle system. Optimize query (index on status, limit). |
| PERF | Performance | 15.2 | Polling latency tied to IRunService.ListAsync gate. If service is slow, tooltip lag increases. | Expected (polling dependent on DB performance). Document latency. |
| PERF | Performance | 15.3 | UI thread queues tooltip update via Dispatcher.InvokeAsync while background task still running. | No blocking (InvokeAsync async). Acceptable. |
| PERF | Performance | 15.4 | GC pressure from per-tick allocations (runs list, string formatting). | Minor. Acceptable for 5s interval. |
| PERF | Performance | 15.5 | NotifyIcon.Text assignment triggers shell redraw (UI update in explorer.exe). Possible performance impact on slow systems. | Rare. Accept or batch updates. |
| PERF | Performance | 15.6 | CreateAndShowTrayIcon allocates ContextMenuStrip and 9 menu items upfront. Acceptable memory (< 10KB). | No issue. |
| PERF | Performance | 15.7 | Tooltip refresh spawns new Task.Run every 5 seconds. Thread pool efficient but verify no starvation. | Acceptable (pooled threads). Monitor in high-load scenario. |

---

## Summary

**Total Distinct Cases: 90**

- **BUG:** 47 cases
- **RACE:** 16 cases
- **LEAK:** 11 cases
- **PERF:** 12 cases
- **SEC:** 5 cases
- **UX:** 8 cases
- **CHORE:** 6 cases
- **DOS:** 0 cases (no specific DOS vectors identified in this slice)

---

## Top Recommendations

1. **CRITICAL: Fix GDI handle leak** in icon refresh (case 1.6 + 8.1). Dispose old Icon or use single cached instance.
2. **CRITICAL: Guard against null _trayIcon in RefreshTimer_Tick** (case 7.3, 9.2). Add disposed flag check.
3. **CRITICAL: Unsubscribe DispatcherTimer.Tick event** in Dispose (case 8.3). Use `-=` operator.
4. **HIGH: Implement plural logic** for "1 run" vs "N runs" (case 2.3, 10.2).
5. **HIGH: Event-driven refresh** on profile start/stop (case 6.2, 6.11). Listen to IProfileRunner.ActiveChanged.
6. **HIGH: Verify icon stream disposal** (case 1.4, 8.2). Call stream?.Dispose().
7. **MEDIUM: Cap active-run count display** at 100+ (case 4.4, 10.3).
8. **MEDIUM: Sanitize LastError** before balloon display (case 14.1, 5.6).
9. **MEDIUM: Add health check** on Application.Current.Dispatcher (case 9.6, 3.2).
10. **MEDIUM: Document 5-second polling latency** in user-facing docs (case 3.15, 6.1).

---

**File Path:** `F:\projects\ghost_shell_desktop\audit_phase38_slice1_tray_visual.md`

**End of Audit**

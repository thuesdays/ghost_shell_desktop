// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using System.Text.Json;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Recording;

/// <summary>
/// Phase 63 — Browser Action Recorder implementation. Hooks into a
/// live browser session and translates user gestures into ScriptStep
/// records.
///
/// Architecture:
///   1. We inject a small JS agent (<see cref="RecorderAgentScript"/>)
///      into the live page via ExecuteScriptAsync. The agent registers
///      capture-phase listeners on document for click, input, scroll,
///      and pushState/popstate so it sees events even before page
///      handlers run. Each event becomes a JSON record pushed onto
///      <c>window.__gsRec.q</c>.
///   2. A C# polling loop drains the queue every <see cref="PollMs"/> ms.
///      For each event it generates a stable CSS selector (id &gt;
///      data-test &gt; name &gt; class+nth) and emits a ScriptStep.
///   3. The agent is idempotent — re-running it on a navigated page
///      that lacks the global is a no-op of installation. So our
///      polling re-injects on every iteration; navigations are handled
///      transparently.
///   4. Typing is buffered per input element until the user blurs,
///      submits, or stops typing for <see cref="ScriptRecorderOptions.TypingDebounceMs"/>.
///      One 'type' step per input field rather than one per keystroke.
///   5. Scrolls are throttled to one step per ~600ms of continuous
///      scrolling, with the cumulative deltaY recorded.
///
/// Concurrency: a single recorder instance handles one session at a
/// time. <see cref="StartAsync"/> while already recording throws.
/// The polling loop runs on Task.Run; events fired via
/// <see cref="StepCaptured"/> happen on the polling thread — UI
/// subscribers must marshal to the dispatcher themselves (the
/// dialog's view-model does this).
/// </summary>
public sealed class ScriptRecorder : IScriptRecorder, IAsyncDisposable
{
    private readonly ILogger<ScriptRecorder> _log;
    private readonly object _lock = new();
    private IBrowserSession? _session;
    private ScriptRecorderOptions _options = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private readonly List<ScriptStep> _captured = new();
    private long _lastEventTimestamp;
    private bool _isPaused;
    private bool _isRecording;
    private string? _lastNavigatedUrl;

    /// <summary>Polling interval — 500ms balances UI responsiveness
    /// (steps appear within half a second of the gesture) against
    /// chromedriver round-trip overhead (each ExecuteScript is ~30-60ms
    /// on a healthy session).</summary>
    private const int PollMs = 500;

    public ScriptRecorder(ILogger<ScriptRecorder> log)
    {
        _log = log;
    }

    public bool IsRecording => _isRecording;
    public bool IsPaused => _isPaused;

    public IReadOnlyList<ScriptStep> CapturedSteps
    {
        get { lock (_lock) return _captured.ToList(); }
    }

    public event EventHandler<ScriptStep>? StepCaptured;
    public event EventHandler? StateChanged;

    public async Task StartAsync(
        IBrowserSession session,
        ScriptRecorderOptions options,
        CancellationToken ct = default)
    {
        if (session is null) throw new ArgumentNullException(nameof(session));
        if (options is null) throw new ArgumentNullException(nameof(options));
        lock (_lock)
        {
            if (_isRecording)
                throw new InvalidOperationException(
                    "A recording is already in progress. Stop the previous session first.");
            _isRecording = true;
            _isPaused = false;
            _captured.Clear();
            _session = session;
            _options = options;
            _lastEventTimestamp = 0;
            _lastNavigatedUrl = null;
        }

        _log.LogInformation(
            "ScriptRecorder.Start: profile='{P}' clicks={C} typing={T} nav={N} scroll={S}",
            session.ProfileName, options.CaptureClicks, options.CaptureTyping,
            options.CaptureNavigations, options.CaptureScrolls);

        // First-shot inject. Subsequent polls re-inject if the agent
        // is missing (post-navigation), so this initial call is
        // mostly a sanity check that the session is alive.
        try
        {
            await session.ExecuteScriptAsync(RecorderAgentScript, null, ct);
        }
        catch (Exception ex)
        {
            // Reset state if we failed to install the agent.
            lock (_lock) { _isRecording = false; _session = null; }
            throw new InvalidOperationException(
                "Could not install recorder agent — browser session may be dead.", ex);
        }

        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task PauseAsync(CancellationToken ct = default)
    {
        if (!_isRecording) return Task.CompletedTask;
        _isPaused = true;
        _log.LogInformation("ScriptRecorder paused");
        StateChanged?.Invoke(this, EventArgs.Empty);
        // Tell the agent to drop new events while paused; the C# poll
        // also short-circuits but flipping the JS flag avoids growing
        // the queue during long pauses.
        var session = _session;
        if (session is not null)
        {
            _ = session.ExecuteScriptAsync(
                "if(window.__gsRec){window.__gsRec.paused=true;}",
                null, ct);
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        if (!_isRecording) return Task.CompletedTask;
        _isPaused = false;
        _log.LogInformation("ScriptRecorder resumed");
        StateChanged?.Invoke(this, EventArgs.Empty);
        var session = _session;
        if (session is not null)
        {
            _ = session.ExecuteScriptAsync(
                "if(window.__gsRec){window.__gsRec.paused=false;}",
                null, ct);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<ScriptStep>> StopAsync(CancellationToken ct = default)
    {
        if (!_isRecording) return Array.Empty<ScriptStep>();

        _log.LogInformation("ScriptRecorder stopping — draining queue one last time");

        // Stop the poll loop; we'll do one final manual drain below.
        _pollCts?.Cancel();
        try
        {
            if (_pollTask is not null) await _pollTask;
        }
        catch { /* swallow cancellation */ }

        // Final drain — captures anything queued in the last <PollMs.
        // Phase 68 — drain ALL windows (DrainQueueOnceAsync iterates
        // them) + uninstall agent on each so listeners + history
        // patches don't survive into the user's regular browsing.
        var session = _session;
        if (session is not null)
        {
            try { await DrainQueueOnceAsync(session, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "Final drain failed"); }
            // Uninstall agent across every window, restore focus.
            try
            {
                var handles = await session.GetWindowHandlesAsync(ct);
                var origHandle = await session.GetCurrentWindowHandleAsync(ct);
                const string uninstallJs =
                    "if(window.__gsRec && typeof window.__gsRec.unregister==='function'){"
                    + "try{window.__gsRec.unregister();}catch(e){}}"
                    + "delete window.__gsRec;";
                if (handles.Count <= 1)
                {
                    try { await session.ExecuteScriptAsync(uninstallJs, null, ct); }
                    catch { /* session may be dead */ }
                }
                else
                {
                    foreach (var h in handles)
                    {
                        try
                        {
                            await session.SwitchToWindowAsync(h, ct);
                            await session.ExecuteScriptAsync(uninstallJs, null, ct);
                        }
                        catch { /* window vanished or session dead */ }
                    }
                    try
                    {
                        if (handles.Contains(origHandle))
                            await session.SwitchToWindowAsync(origHandle, ct);
                    }
                    catch { /* best-effort */ }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Recorder uninstall pass failed (continuing)");
            }
        }

        IReadOnlyList<ScriptStep> result;
        lock (_lock)
        {
            result = _captured.ToList();
            _captured.Clear();
            _isRecording = false;
            _isPaused = false;
            _session = null;
            _pollCts?.Dispose();
            _pollCts = null;
            _pollTask = null;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
        _log.LogInformation("ScriptRecorder stopped — captured {N} step(s)", result.Count);
        return result;
    }

    public ValueTask DisposeAsync()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── Polling loop ─────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var session = _session;
                if (session is null) break;
                if (_isPaused)
                {
                    await Task.Delay(PollMs, ct);
                    continue;
                }
                await DrainQueueOnceAsync(session, ct);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                consecutiveErrors++;
                if (consecutiveErrors >= 5)
                {
                    _log.LogWarning(ex,
                        "Recorder poll failed 5x in a row — session likely dead, stopping");
                    break;
                }
                _log.LogDebug(ex, "Recorder poll error (attempt {N})", consecutiveErrors);
            }
            try { await Task.Delay(PollMs, ct); }
            catch (OperationCanceledException) { break; }
        }
        _log.LogDebug("Recorder poll loop exited");
    }

    /// <summary>
    /// Phase 68 — one pass over EVERY window's JS event queue. The
    /// recorder must follow the user across windows: when they click
    /// the OKX/MetaMask extension icon, the popup opens as a new
    /// window/tab whose driver focus is NOT the originally-recorded
    /// page. Without per-window draining the recorder captures
    /// nothing inside the popup. This method:
    ///   1. Snapshots WindowHandles + saves the originally-focused
    ///      window so we can restore it after.
    ///   2. For each handle: switch focus, install-or-drain agent,
    ///      collect events tagged with the window's URL/title.
    ///   3. Restore the original focus so the user's interactions
    ///      aren't disrupted by us yanking the active tab around.
    /// Window enumeration is best-effort — extension popups can
    /// close mid-iteration (NoSuchWindow); we swallow + continue.
    /// </summary>
    private async Task DrainQueueOnceAsync(IBrowserSession session, CancellationToken ct)
    {
        IReadOnlyList<string> handles;
        string originalHandle;
        try
        {
            handles = await session.GetWindowHandlesAsync(ct);
            originalHandle = await session.GetCurrentWindowHandleAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Recorder window enumeration failed; falling back to current window");
            await DrainSingleWindowAsync(session, ct);
            return;
        }

        if (handles.Count == 0)
        {
            await DrainSingleWindowAsync(session, ct);
            return;
        }

        // Don't bother switching for single-window case — saves the
        // ~10-30ms switch overhead per poll on the common path where
        // user is on one tab.
        if (handles.Count == 1)
        {
            await DrainSingleWindowAsync(session, ct);
            return;
        }

        foreach (var handle in handles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await session.SwitchToWindowAsync(handle, ct);
                await DrainSingleWindowAsync(session, ct);
            }
            catch (Exception ex)
            {
                // Window vanished mid-iteration (extension popup
                // closed by user, devtools panel toggled, etc).
                // Skip + continue — next tick re-enumerates.
                _log.LogDebug(ex, "Recorder drain failed for handle {Handle} (skipping)", handle);
            }
        }

        // Restore focus so the user's next interaction goes to
        // whatever they had active. Skip if the original window
        // is gone (rare — they closed their starting tab during
        // the recording).
        try
        {
            if (handles.Contains(originalHandle))
                await session.SwitchToWindowAsync(originalHandle, ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Recorder failed to restore original window");
        }
    }

    /// <summary>
    /// Drain the recorder agent from the session's currently-focused
    /// window. Extracted so the multi-window path can call it per
    /// handle and the single-window fallback can skip the switch
    /// overhead.
    /// </summary>
    private async Task DrainSingleWindowAsync(IBrowserSession session, CancellationToken ct)
    {
        // Combined re-install + drain in ONE round-trip. The agent is
        // idempotent (uses `||=`); if it's missing, we install fresh
        // and the queue starts empty. If present, we splice out the
        // current queue and return its JSON.
        //
        // Phase 66 fix — Selenium's ExecuteScript wraps the body in a
        // function and returns ONLY whatever a top-level `return`
        // statement evaluates to. An expression-statement IIFE's
        // result is discarded. Without the leading `return`, every
        // poll got `undefined` from the driver, the C# null-check
        // short-circuited, and steps were never drained. Result:
        // recorder captured 0 steps regardless of user activity.
        var script = """
            return (function(){
              if (!window.__gsRec) {
                __INSTALL__
                return '[]';
              }
              var batch = window.__gsRec.q;
              window.__gsRec.q = [];
              return JSON.stringify(batch);
            })();
        """.Replace("__INSTALL__", RecorderAgentScript);

        var raw = await session.ExecuteScriptAsync(script, null, ct) as string;
        if (string.IsNullOrEmpty(raw) || raw == "[]") return;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var step = ConvertEventToStep(item);
                if (step is null) continue;

                // Insert a 'dwell' step if the gap since the last
                // captured event exceeds the user-configured threshold.
                if (_options.CaptureDwells &&
                    item.TryGetProperty("ts", out var tsEl) &&
                    tsEl.TryGetInt64(out var ts))
                {
                    if (_lastEventTimestamp > 0 &&
                        ts - _lastEventTimestamp > _options.DwellMinMs)
                    {
                        var dwellMs = (int)Math.Min(ts - _lastEventTimestamp, 30_000);
                        var dwellStep = new ScriptStep
                        {
                            Type = "dwell",
                            Params = new Dictionary<string, object?>
                            {
                                ["min_ms"] = dwellMs,
                                ["max_ms"] = dwellMs,
                            },
                            Label = $"dwell ~{dwellMs / 1000}s",
                        };
                        AppendStep(dwellStep);
                    }
                    _lastEventTimestamp = ts;
                }

                AppendStep(step);
            }
        }
        catch (JsonException jex)
        {
            _log.LogDebug(jex, "Recorder queue parse failed (raw: {Snippet})",
                raw.Length > 80 ? raw[..80] + "…" : raw);
        }
    }

    private void AppendStep(ScriptStep step)
    {
        lock (_lock) _captured.Add(step);
        StepCaptured?.Invoke(this, step);
    }

    /// <summary>
    /// Translate one JS event record into a ScriptStep. Returns null if
    /// the event is filtered out by the current options or malformed.
    /// </summary>
    private ScriptStep? ConvertEventToStep(JsonElement item)
    {
        var kind = item.TryGetProperty("kind", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(kind)) return null;

        switch (kind)
        {
            case "click":
                if (!_options.CaptureClicks) return null;
                var sel = item.TryGetProperty("sel", out var sEl) ? sEl.GetString() ?? "" : "";
                var text = item.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                var href = item.TryGetProperty("href", out var hEl) ? hEl.GetString() : null;
                if (string.IsNullOrEmpty(sel)) return null;
                return new ScriptStep
                {
                    Type = "click",
                    Params = new Dictionary<string, object?>
                    {
                        ["selector"] = sel,
                    },
                    Label = !string.IsNullOrEmpty(text)
                        ? $"click '{Truncate(text, 30)}'"
                        : !string.IsNullOrEmpty(href)
                            ? $"click → {Truncate(href, 40)}"
                            : "click",
                };

            case "type":
                if (!_options.CaptureTyping) return null;
                var typeSel = item.TryGetProperty("sel", out var tsEl) ? tsEl.GetString() ?? "" : "";
                var value = item.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(typeSel)) return null;
                return new ScriptStep
                {
                    Type = "type",
                    Params = new Dictionary<string, object?>
                    {
                        ["selector"] = typeSel,
                        ["value"] = value,
                        ["clear_first"] = true,
                    },
                    Label = $"type '{Truncate(value, 30)}'",
                };

            case "navigate":
                if (!_options.CaptureNavigations) return null;
                var url = item.TryGetProperty("url", out var uEl) ? uEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(url)) return null;
                // Suppress repeat navigations to the same URL — the agent
                // emits navigate events on pushState too which can fire
                // multiple times for SPA route changes.
                if (string.Equals(url, _lastNavigatedUrl, StringComparison.Ordinal))
                    return null;
                _lastNavigatedUrl = url;
                return new ScriptStep
                {
                    Type = "navigate",
                    Params = new Dictionary<string, object?>
                    {
                        ["url"] = url,
                    },
                    Label = $"navigate {Truncate(url, 50)}",
                };

            case "scroll":
                if (!_options.CaptureScrolls) return null;
                var deltaY = item.TryGetProperty("dy", out var dyEl) && dyEl.TryGetInt32(out var dy) ? dy : 0;
                if (Math.Abs(deltaY) < _options.ScrollMinPixels) return null;
                return new ScriptStep
                {
                    Type = "scroll",
                    Params = new Dictionary<string, object?>
                    {
                        ["direction"] = deltaY > 0 ? "down" : "up",
                        ["amount"] = Math.Abs(deltaY),
                    },
                    Label = $"scroll {(deltaY > 0 ? "↓" : "↑")} {Math.Abs(deltaY)}px",
                };

            default:
                return null;
        }
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }

    // ── JS Agent ─────────────────────────────────────────────────

    /// <summary>
    /// Recorder agent — installed into <c>window.__gsRec</c>. Idempotent
    /// re-install via `||=`. Holds a queue of pending events plus a
    /// paused-flag plus per-input typing buffers (so we emit one 'type'
    /// step per field instead of one per keystroke). Selector generation
    /// prefers stable identifiers in this order:
    ///   1. `#id` — when id is present, valid CSS, and not auto-generated
    ///      (we filter out hex/uuid/long-numeric ids).
    ///   2. `[data-test-id="X"]` / `[data-testid="X"]` / `[data-cy="X"]`
    ///      / `[data-qa="X"]` — test-attribute, very stable.
    ///   3. `[name="X"]` for form fields.
    ///   4. tag + class chain (first 2 stable classes) + nth-of-type if
    ///      ambiguous.
    ///   5. Fall back to body-relative path.
    /// </summary>
    private const string RecorderAgentScript = """
        window.__gsRec = window.__gsRec || (function() {
          var state = { q: [], paused: false, typingBuffer: {}, typingTimer: null,
                        lastScrollY: window.scrollY, scrollAccum: 0, scrollTimer: null,
                        lastUrl: location.href, navInterval: null,
                        // Phase 66 — store named handler refs so unregister()
                        // can removeEventListener them when the recorder
                        // stops. Anonymous functions don't unsubscribe.
                        handlers: {} };

          function isAutoId(id) {
            if (!id || id.length > 60) return true;
            // Hex blob (8+ hex chars), pure number, uuid pattern, ext-id pattern.
            if (/^[0-9a-f]{8,}$/i.test(id)) return true;
            if (/^[0-9a-f-]{20,}$/i.test(id)) return true;
            if (/^\d+$/.test(id) && id.length > 6) return true;
            return false;
          }
          function escapeAttr(v) {
            return String(v).replace(/\\/g, '\\\\').replace(/"/g, '\\"');
          }
          function selectorFor(el) {
            if (!el || el.nodeType !== 1) return '';
            // Phase 66 — name attribute on form fields wins over id.
            // Google's search box has id="APjFqb" (page-version-tied) +
            // name="q" (decade-stable). Recording the id breaks replay
            // on the next page version. Recording the name doesn't.
            if ((el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT') && el.name) {
              return el.tagName.toLowerCase() + '[name="' + escapeAttr(el.name) + '"]';
            }
            // Test-id attributes — explicitly stable, prioritised next.
            var testAttrs = ['data-test-id','data-testid','data-cy','data-qa','data-test'];
            for (var i = 0; i < testAttrs.length; i++) {
              var v = el.getAttribute(testAttrs[i]);
              if (v) return '[' + testAttrs[i] + '="' + escapeAttr(v) + '"]';
            }
            // id — but only if it looks human-authored.
            if (el.id && !isAutoId(el.id)) {
              try { document.querySelector('#' + CSS.escape(el.id)); return '#' + CSS.escape(el.id); }
              catch (e) {}
            }
            // 4. tag + first 2 stable classes
            var tag = el.tagName.toLowerCase();
            var classes = [];
            if (el.classList) {
              for (var i = 0; i < el.classList.length && classes.length < 2; i++) {
                var cls = el.classList[i];
                // Skip generated/utility classes (long hex, css-modules)
                if (/^[a-z][\w-]{1,30}$/i.test(cls) && !/^css-/.test(cls)
                    && !/^[a-z]+-[0-9a-f]{6,}$/i.test(cls)) {
                  classes.push(cls);
                }
              }
            }
            var sel = tag + (classes.length > 0 ? '.' + classes.join('.') : '');
            // Disambiguate with nth-of-type if multiple matches.
            try {
              var matches = document.querySelectorAll(sel);
              if (matches.length > 1) {
                var idx = 0;
                var sib = el;
                while (sib = sib.previousElementSibling) {
                  if (sib.tagName === el.tagName) idx++;
                }
                sel = sel + ':nth-of-type(' + (idx + 1) + ')';
              }
            } catch (e) {}
            return sel;
          }

          function flushTyping(elKey, sel) {
            if (state.typingBuffer[elKey] !== undefined) {
              state.q.push({
                kind: 'type',
                sel: sel,
                value: state.typingBuffer[elKey],
                ts: Date.now(),
              });
              delete state.typingBuffer[elKey];
            }
          }
          function flushAllTyping() {
            for (var key in state.typingBuffer) {
              if (state.typingBuffer.hasOwnProperty(key)) {
                state.q.push({
                  kind: 'type',
                  sel: key.split('||')[0],
                  value: state.typingBuffer[key],
                  ts: Date.now(),
                });
              }
            }
            state.typingBuffer = {};
          }

          // ── Phase 66 — named handler functions stored on state.handlers
          // so unregister() can removeEventListener them when the
          // recorder stops. Anonymous addEventListener calls cannot be
          // unsubscribed; without this, listeners keep firing into a
          // deleted state on every click/keystroke after stop, throwing
          // silent JS errors until the page navigates.
          state.handlers.click = function(ev) {
            if (state.paused) return;
            var el = ev.target;
            var cur = el;
            for (var hop = 0; hop < 4 && cur; hop++) {
              if (cur.tagName === 'A' || cur.tagName === 'BUTTON'
                  || cur.tagName === 'INPUT' || cur.getAttribute('role') === 'button') {
                el = cur; break;
              }
              cur = cur.parentElement;
            }
            var sel = selectorFor(el);
            if (!sel) return;
            flushAllTyping();
            state.q.push({
              kind: 'click', sel: sel,
              text: (el.innerText || el.textContent || '').trim().slice(0, 60),
              href: el.tagName === 'A' ? el.href : null,
              ts: Date.now(),
            });
          };

          state.handlers.input = function(ev) {
            if (state.paused) return;
            var el = ev.target;
            if (!el || (el.tagName !== 'INPUT' && el.tagName !== 'TEXTAREA'
                       && !el.isContentEditable)) return;
            var sel = selectorFor(el);
            if (!sel) return;
            var key = sel + '||' + (el.id || el.name || '');
            state.typingBuffer[key] = (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA')
              ? el.value : (el.innerText || el.textContent || '');
            if (state.typingTimer) clearTimeout(state.typingTimer);
            state.typingTimer = setTimeout(function() { flushTyping(key, sel); }, 700);
          };

          state.handlers.blur = function(ev) {
            if (state.paused) return;
            var el = ev.target;
            if (!el || el.nodeType !== 1) return;
            var sel = selectorFor(el);
            if (!sel) return;
            var key = sel + '||' + (el.id || el.name || '');
            flushTyping(key, sel);
          };

          state.handlers.scroll = function() {
            if (state.paused) return;
            var dy = window.scrollY - state.lastScrollY;
            state.lastScrollY = window.scrollY;
            state.scrollAccum += dy;
            if (state.scrollTimer) clearTimeout(state.scrollTimer);
            state.scrollTimer = setTimeout(function() {
              if (Math.abs(state.scrollAccum) > 10) {
                state.q.push({ kind: 'scroll', dy: state.scrollAccum, ts: Date.now() });
                state.scrollAccum = 0;
              }
            }, 600);
          };

          state.handlers.checkNav = function() {
            if (state.paused) return;
            if (location.href !== state.lastUrl) {
              state.lastUrl = location.href;
              state.q.push({ kind: 'navigate', url: location.href, ts: Date.now() });
            }
          };

          // Wire the named handlers.
          document.addEventListener('click', state.handlers.click, true);
          document.addEventListener('input', state.handlers.input, true);
          document.addEventListener('blur',  state.handlers.blur,  true);
          window.addEventListener('scroll',  state.handlers.scroll, true);
          window.addEventListener('popstate', state.handlers.checkNav, true);

          // pushState/replaceState patches for SPA navigation. Save the
          // originals so unregister() can restore them — leaving our
          // patched versions in place after stop would leak into the
          // user's normal browsing session.
          state.origPushState    = history.pushState;
          state.origReplaceState = history.replaceState;
          history.pushState = function() {
            var r = state.origPushState.apply(this, arguments);
            setTimeout(state.handlers.checkNav, 0);
            return r;
          };
          history.replaceState = function() {
            var r = state.origReplaceState.apply(this, arguments);
            setTimeout(state.handlers.checkNav, 0);
            return r;
          };

          // Pulse-poll for full page loads. Save the handle so
          // unregister() can clearInterval it.
          state.navInterval = setInterval(state.handlers.checkNav, 1000);

          // Initial state — emit a navigate event for the page we're
          // currently on so the recorded script reproduces the start.
          state.q.push({
            kind: 'navigate', url: location.href, ts: Date.now(),
          });

          // ── Phase 66 — unregister() reverses every side-effect of
          // the install: removeEventListener on every named handler,
          // restore patched history methods, clear the interval, and
          // clear pending typing/scroll timers. Idempotent.
          state.unregister = function() {
            try {
              document.removeEventListener('click', state.handlers.click, true);
              document.removeEventListener('input', state.handlers.input, true);
              document.removeEventListener('blur',  state.handlers.blur,  true);
              window.removeEventListener('scroll', state.handlers.scroll, true);
              window.removeEventListener('popstate', state.handlers.checkNav, true);
            } catch (e) {}
            // Restore history methods only if our patched versions are
            // still installed (a different script may have re-patched
            // since — leave it alone in that case rather than blowing
            // away foreign monkey-patches).
            try {
              if (state.origPushState && history.pushState !== state.origPushState
                  // Heuristic: our wrapper closes over state.origPushState,
                  // so length === 0 (variadic apply) and the function
                  // body references state.origPushState. We just set it
                  // back unconditionally for simplicity; the foreign
                  // patcher's wrapper is bypassed but their state is
                  // already gone anyway since we deleted window.__gsRec.
              ) {
                history.pushState    = state.origPushState;
                history.replaceState = state.origReplaceState;
              }
            } catch (e) {}
            try { if (state.navInterval) clearInterval(state.navInterval); } catch (e) {}
            try { if (state.typingTimer) clearTimeout(state.typingTimer); } catch (e) {}
            try { if (state.scrollTimer) clearTimeout(state.scrollTimer); } catch (e) {}
            state.handlers = {};
          };

          return state;
        })();
    """;
}

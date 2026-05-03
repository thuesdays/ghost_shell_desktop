// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 63 — Browser Action Recorder. Hooks into a live browser
/// session via <see cref="IBrowserSession.ExecuteScriptAsync"/> and
/// translates user gestures (clicks, typing, navigations, scrolls)
/// into a sequence of <see cref="ScriptStep"/> records that can be
/// saved as a Script.
///
/// Lifecycle: <c>StartAsync</c> → user interacts with the page →
/// <c>PauseAsync</c>/<c>ResumeAsync</c> as needed → <c>StopAsync</c>
/// returns the captured step list. The recorder injects a small JS
/// agent into the live page that buffers events into a window-level
/// queue; the C# side polls the queue every ~500ms, generates stable
/// CSS selectors, and emits one ScriptStep per gesture (with debounced
/// typing collected into a single 'type' step per input field).
///
/// The recorder is single-instance — only one recording session per
/// app process. Calling StartAsync while another session is active
/// throws InvalidOperationException.
/// </summary>
public interface IScriptRecorder
{
    /// <summary>
    /// Begin capturing actions from <paramref name="session"/>. The
    /// session must already be navigated to a real page (the recorder
    /// can't bootstrap navigation itself; callers do that). The
    /// <paramref name="options"/> control which gesture types fire
    /// steps and how aggressive the typing-debounce is.
    /// </summary>
    Task StartAsync(
        IBrowserSession session,
        ScriptRecorderOptions options,
        CancellationToken ct = default);

    /// <summary>Stop emitting events into the buffer without tearing
    /// down the JS agent. Resume picks up from where Pause left off.</summary>
    Task PauseAsync(CancellationToken ct = default);

    /// <summary>Re-enable event emission after a pause.</summary>
    Task ResumeAsync(CancellationToken ct = default);

    /// <summary>
    /// Finalise the recording, drain the JS event queue one last time,
    /// remove the JS agent, and return the captured steps. The recorder
    /// returns to Idle after this call so a fresh StartAsync is allowed.
    /// </summary>
    Task<IReadOnlyList<ScriptStep>> StopAsync(CancellationToken ct = default);

    /// <summary>True between StartAsync and StopAsync.</summary>
    bool IsRecording { get; }

    /// <summary>True when paused.</summary>
    bool IsPaused { get; }

    /// <summary>
    /// Live view of captured steps. Updates as the user interacts
    /// with the page. The UI binds to this for a "step preview"
    /// panel during recording.
    /// </summary>
    IReadOnlyList<ScriptStep> CapturedSteps { get; }

    /// <summary>Fired on the polling thread each time a new step is
    /// captured. UI subscribers must marshal to the dispatcher.</summary>
    event EventHandler<ScriptStep>? StepCaptured;

    /// <summary>Fired when the recorder transitions between states
    /// (Idle / Recording / Paused / Stopped). UI uses this to refresh
    /// the recording badge + button enabled-state.</summary>
    event EventHandler? StateChanged;
}

/// <summary>
/// Options controlling what the recorder captures. Defaults are tuned
/// for the SERP-engagement workflow: clicks + typing + navigation,
/// scrolls collected at low frequency, dwells inserted between gestures.
/// </summary>
public sealed class ScriptRecorderOptions
{
    /// <summary>Capture click events on anchors, buttons, generic
    /// elements with click handlers. Default true.</summary>
    public bool CaptureClicks { get; init; } = true;

    /// <summary>Capture text input on &lt;input&gt;, &lt;textarea&gt;,
    /// contenteditable. Default true. Text events are debounced — we
    /// emit one 'type' step per field once the user moves focus or
    /// pauses typing for &gt; <see cref="TypingDebounceMs"/>.</summary>
    public bool CaptureTyping { get; init; } = true;

    /// <summary>Emit a 'navigate' step when the URL changes. Default
    /// true — without this the script can't reproduce visit order.</summary>
    public bool CaptureNavigations { get; init; } = true;

    /// <summary>Emit a 'scroll' step when the user scrolls more than
    /// <see cref="ScrollMinPixels"/>. Default true. Scroll events are
    /// throttled — the recorder collects scroll deltas and emits one
    /// step per ~600ms of continuous scrolling.</summary>
    public bool CaptureScrolls { get; init; } = true;

    /// <summary>Emit a 'dwell' step between gestures whose gap exceeds
    /// <see cref="DwellMinMs"/>. Default true — preserves the natural
    /// pacing of the recording for replay.</summary>
    public bool CaptureDwells { get; init; } = true;

    /// <summary>Min pause (ms) between gestures before a dwell is
    /// inserted. Default 1500ms.</summary>
    public int DwellMinMs { get; init; } = 1500;

    /// <summary>Min pixels of scroll movement before a 'scroll' step
    /// fires. Default 80px.</summary>
    public int ScrollMinPixels { get; init; } = 80;

    /// <summary>Idle window (ms) after which queued typing is flushed
    /// to a 'type' step. Default 700ms. Lower = more granular steps;
    /// higher = fewer, larger 'type' calls.</summary>
    public int TypingDebounceMs { get; init; } = 700;
}

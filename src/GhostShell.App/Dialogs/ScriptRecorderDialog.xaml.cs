// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Phase 63 — Browser Action Recorder modal. Composes:
///   • Profile picker + start URL → opens browser session
///   • Live step list bound to <see cref="IScriptRecorder.CapturedSteps"/>
///   • Pause/Resume/Stop controls
///   • Save-as field that writes a new <see cref="Script"/> via IScriptService
///
/// The dialog owns the recorder lifecycle: StartAsync drives both the
/// browser launch + recorder.StartAsync; StopAsync drains and tears
/// everything down. If the user closes the dialog mid-recording we
/// stop gracefully and abandon the captured steps.
/// </summary>
public partial class ScriptRecorderDialog : Window
{
    private bool _teardownDone;

    /// <summary>The created script. Phase 66 — captured directly on
    /// the dialog instance the moment SaveAsync persists, instead of
    /// reading through the DataContext at close-time. WPF can null
    /// out DataContext during teardown which made the original
    /// passthrough property return null even after a successful save,
    /// which is why the Scripts page wouldn't refresh.</summary>
    private Script? _createdScript;

    public ScriptRecorderDialog(ScriptRecorderViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += (_, ok) =>
        {
            // Capture the script BEFORE Close() — the VM still has it
            // here. After Close + teardown, DataContext may have been
            // cleared and the passthrough getter would return null.
            if (ok) _createdScript = vm.CreatedScript;
            DialogResult = ok;
            Close();
        };
        // Phase 65 — WPF Closing is synchronous. An async lambda here is
        // fire-and-forget; the window proceeds to close BEFORE the awaited
        // teardown completes, racing the recorder's polling loop against
        // disposed state. Fix: cancel the close, run async cleanup, then
        // close again (with a flag to break the loop).
        Closing += (sender, args) =>
        {
            if (_teardownDone) return; // second pass — let it close
            args.Cancel = true;
            _ = TeardownAndCloseAsync(vm);
        };
    }

    private async Task TeardownAndCloseAsync(ScriptRecorderViewModel vm)
    {
        try { await vm.OnWindowClosingAsync(); }
        catch { /* swallow — best-effort */ }
        _teardownDone = true;
        // Re-issue close on the dispatcher; the second Closing handler
        // sees _teardownDone=true and lets the window close cleanly.
        // Discard the DispatcherOperation — we don't need to await it
        // (the lambda runs fire-and-forget on the UI thread).
        _ = Dispatcher.BeginInvoke(() => Close());
    }

    /// <summary>The created script (set when the user clicks Save).
    /// Null if cancelled. Reads from the cached field, not via
    /// DataContext passthrough — see <see cref="_createdScript"/> for
    /// rationale.</summary>
    public Script? CreatedScript => _createdScript;
}

/// <summary>
/// View-model for <see cref="ScriptRecorderDialog"/>. Drives the
/// recorder + script-saving flow. Marshals StepCaptured events from
/// the recorder's polling thread to the UI dispatcher.
/// </summary>
public sealed partial class ScriptRecorderViewModel : ObservableObject
{
    private readonly IScriptRecorder _recorder;
    private readonly IProfileService _profiles;
    private readonly IProfileRunner _runner;
    private readonly IScriptService _scripts;
    private readonly ILogger<ScriptRecorderViewModel> _log;

    private bool _weStartedTheBrowser;

    public event EventHandler<bool>? RequestClose;

    public ObservableCollection<string> ProfileNames { get; } = new();
    public ObservableCollection<RecordedStepRow> CapturedSteps { get; } = new();

    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string  _startUrl = "https://www.google.com";
    [ObservableProperty] private string  _scriptName = "Recorded script";
    [ObservableProperty] private string  _statusLine = "ready";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private bool _isRecording;

    [ObservableProperty] private bool _isPaused;

    public bool IsIdle => !IsRecording;
    public bool HasCaptured => CapturedSteps.Count > 0;
    public bool CanSave => HasCaptured && !IsRecording && !string.IsNullOrWhiteSpace(ScriptName);
    public int CapturedCount => CapturedSteps.Count;

    /// <summary>The created Script after Save. Null if user cancelled.</summary>
    public Script? CreatedScript { get; private set; }

    public ScriptRecorderViewModel(
        IScriptRecorder recorder,
        IProfileService profiles,
        IProfileRunner runner,
        IScriptService scripts,
        ILogger<ScriptRecorderViewModel> log)
    {
        _recorder = recorder;
        _profiles = profiles;
        _runner   = runner;
        _scripts  = scripts;
        _log      = log;

        _recorder.StepCaptured += OnStepCaptured;
        _recorder.StateChanged += OnRecorderStateChanged;
    }

    public async Task LoadProfileNamesAsync()
    {
        var list = await _profiles.ListAsync();
        ProfileNames.Clear();
        foreach (var p in list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
            ProfileNames.Add(p.Name);
        if (ProfileNames.Count > 0 && string.IsNullOrEmpty(SelectedProfile))
            SelectedProfile = ProfileNames[0];
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile))
        {
            StatusLine = "pick a profile first";
            return;
        }
        if (string.IsNullOrWhiteSpace(StartUrl))
        {
            StatusLine = "enter a start URL";
            return;
        }
        if (IsRecording) return;

        try
        {
            CapturedSteps.Clear();
            OnPropertyChanged(nameof(HasCaptured));
            OnPropertyChanged(nameof(CapturedCount));

            // Launch the profile if it isn't already running. Pass
            // runAssignedScript=false so the user's bound script doesn't
            // race the recording. Phase 65 — set _weStartedTheBrowser
            // ONLY AFTER successful start; on failure the flag stays
            // false so the cleanup path doesn't try to stop a profile
            // that never started (which would otherwise stop a different
            // profile or fire a misleading "stop" log entry).
            if (!_runner.ActiveProfileNames.Contains(SelectedProfile))
            {
                var profile = await _profiles.GetAsync(SelectedProfile);
                if (profile is null) { StatusLine = "profile not found"; return; }
                try
                {
                    _ = await _runner.StartAsync(profile, ct: default,
                        runAssignedScript: false);
                    _weStartedTheBrowser = true;
                }
                catch
                {
                    _weStartedTheBrowser = false;
                    throw;
                }
                // Give chromedriver a moment to spin up before we hijack.
                await Task.Delay(2000);
            }

            var session = _runner.TryGetActiveSession(SelectedProfile);
            if (session is null) { StatusLine = "session not ready"; return; }

            // Navigate to the chosen start URL — gives a clean entry
            // point. The recorder also captures the navigate event.
            try { await session.NavigateAsync(StartUrl); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Recorder: initial navigate failed; recording continues");
            }

            await _recorder.StartAsync(session, new ScriptRecorderOptions());
            IsRecording = true;
            StatusLine = "recording — interact with the browser";
            _log.LogInformation("Recorder started on profile '{P}', url={Url}",
                SelectedProfile, StartUrl);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recorder start failed");
            StatusLine = "start failed: " + Truncate(ex.Message, 60);
        }
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        if (!IsRecording || IsPaused) return;
        await _recorder.PauseAsync();
        IsPaused = true;
        StatusLine = "paused";
    }

    [RelayCommand]
    private async Task ResumeAsync()
    {
        if (!IsRecording || !IsPaused) return;
        await _recorder.ResumeAsync();
        IsPaused = false;
        StatusLine = "recording — interact with the browser";
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!IsRecording) return;
        try
        {
            var steps = await _recorder.StopAsync();
            IsRecording = false;
            IsPaused = false;
            StatusLine = $"captured {steps.Count} step(s) — review and Save";
            // Final reconciliation — there may have been steps drained
            // in the final pass that we haven't received via the event yet.
            ReconcileSteps(steps);
            // Auto-fill script name from first navigate target if user
            // hasn't customised it.
            if (ScriptName == "Recorded script" && steps.Count > 0)
            {
                var firstNav = steps.FirstOrDefault(s => s.Type == "navigate");
                if (firstNav?.Params.TryGetValue("url", out var urlObj) == true
                    && urlObj is string url
                    && Uri.TryCreate(url, UriKind.Absolute, out var u))
                {
                    ScriptName = $"Recorded — {u.Host}";
                }
            }
            // Tear down browser if we launched it for the recording.
            if (_weStartedTheBrowser && !string.IsNullOrEmpty(SelectedProfile))
            {
                try { await _runner.StopAsync(SelectedProfile); }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Couldn't close browser after recording stopped");
                }
                _weStartedTheBrowser = false;
            }
            OnPropertyChanged(nameof(HasCaptured));
            OnPropertyChanged(nameof(CapturedCount));
            OnPropertyChanged(nameof(CanSave));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recorder stop failed");
            StatusLine = "stop failed: " + Truncate(ex.Message, 60);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!CanSave) return;
        try
        {
            // Materialise the live row VMs back into ScriptStep records.
            // CapturedSteps holds the already-converted steps from the
            // recorder; we just grab them in order.
            var steps = CapturedSteps.Select(r => r.Step).ToList();
            // Phase 66 — the editor's canonical on-disk format uses
            // snake_case_lower for multi-word fields (abort_on_error,
            // skip_on_my_domain, ...) and lowercase for single words
            // (type, params, enabled). System.Text.Json defaults to
            // PascalCase, which the editor rejects. CamelCase only
            // works for single-word fields (Type→type) and produces
            // wrong shapes for multi-word ones (AbortOnError→abortOnError
            // ≠ abort_on_error). SnakeCaseLower (.NET 8) matches the
            // editor's writer exactly.
            var json = JsonSerializer.Serialize(steps, new JsonSerializerOptions
            {
                WriteIndented        = false,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            var seed = new Script
            {
                Id          = 0,
                Name        = ScriptName.Trim(),
                Description = $"Recorded from profile '{SelectedProfile}' at {DateTime.Now:yyyy-MM-dd HH:mm}",
                StepsJson   = json,
                Enabled     = true,
                IsDefault   = false,
                ETag        = "",
                CreatedAt   = default,
                UpdatedAt   = default,
                LayoutMode  = "list",
            };
            CreatedScript = await _scripts.CreateAsync(seed);
            _log.LogInformation(
                "Recorded script saved: id={Id}, {N} step(s)",
                CreatedScript.Id, steps.Count);
            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Recorder save failed");
            StatusLine = "save failed: " + Truncate(ex.Message, 60);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(this, false);
    }

    /// <summary>
    /// Called by the Window's Closing handler — make sure we tear down
    /// the recorder + browser even on accidental close. Idempotent.
    /// </summary>
    public async Task OnWindowClosingAsync()
    {
        try
        {
            if (IsRecording) { await _recorder.StopAsync(); }
            if (_weStartedTheBrowser && !string.IsNullOrEmpty(SelectedProfile))
            {
                try { await _runner.StopAsync(SelectedProfile); } catch { }
                _weStartedTheBrowser = false;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Recorder dialog teardown error (non-fatal)");
        }
        // Detach event handlers so the singleton recorder doesn't
        // hold a strong ref to a closed dialog VM.
        _recorder.StepCaptured -= OnStepCaptured;
        _recorder.StateChanged -= OnRecorderStateChanged;
    }

    private void OnStepCaptured(object? sender, ScriptStep step)
    {
        // Marshal to UI thread — recorder fires from polling thread.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var row = RecordedStepRow.From(step, CapturedSteps.Count + 1);
            CapturedSteps.Add(row);
            OnPropertyChanged(nameof(CapturedCount));
            OnPropertyChanged(nameof(HasCaptured));
            StatusLine = $"recording — {CapturedSteps.Count} step(s) captured";
        });
    }

    private void OnRecorderStateChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Nothing to do here yet — IsRecording / IsPaused are
            // already driven by the explicit commands. This event
            // exists for future extensibility.
        });
    }

    /// <summary>
    /// Reconcile our locally-tracked CapturedSteps against the final
    /// list returned by StopAsync. The recorder may have drained a few
    /// extra steps in its final pass that we haven't received via the
    /// StepCaptured event yet (race between Stop and the last poll).
    /// </summary>
    private void ReconcileSteps(IReadOnlyList<ScriptStep> finalSteps)
    {
        if (finalSteps.Count <= CapturedSteps.Count) return;
        for (int i = CapturedSteps.Count; i < finalSteps.Count; i++)
        {
            CapturedSteps.Add(RecordedStepRow.From(finalSteps[i], i + 1));
        }
        OnPropertyChanged(nameof(CapturedCount));
        OnPropertyChanged(nameof(HasCaptured));
    }

    partial void OnScriptNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}

/// <summary>
/// Display row for one captured step. Exposes a <see cref="TypeBrush"/>
/// for the type-pill colour and a <see cref="Description"/> string
/// derived from the step's Label / params for the main text column.
/// </summary>
public sealed class RecordedStepRow
{
    public required ScriptStep Step { get; init; }
    public required int Index { get; init; }
    public required string Type { get; init; }
    public required string Description { get; init; }
    public required Brush TypeBrush { get; init; }

    public static RecordedStepRow From(ScriptStep s, int index)
    {
        var brush = s.Type switch
        {
            "click"    => new SolidColorBrush(Color.FromRgb(0x2D, 0xD4, 0xBF)),  // teal
            "type"     => new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)),  // blue
            "navigate" => new SolidColorBrush(Color.FromRgb(0xA8, 0x78, 0xFA)),  // purple
            "scroll"   => new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),  // slate
            "dwell"    => new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7A)),  // grey
            _          => new SolidColorBrush(Color.FromRgb(0x52, 0x52, 0x5B)),
        };
        var desc = !string.IsNullOrEmpty(s.Label)
            ? s.Label!
            : DescribeStep(s);
        return new RecordedStepRow
        {
            Step = s,
            Index = index,
            Type = s.Type,
            Description = desc,
            TypeBrush = brush,
        };
    }

    private static string DescribeStep(ScriptStep s)
    {
        if (s.Params.Count == 0) return s.Type;
        var first = s.Params.First();
        return $"{first.Key}={first.Value}";
    }
}

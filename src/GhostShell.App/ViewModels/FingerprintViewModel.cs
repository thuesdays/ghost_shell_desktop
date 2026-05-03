// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Dialogs;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

/// <summary>
/// Fingerprint page VM. Mirrors the legacy web's coherence-validator
/// surface: per-profile score (overall + 4 sub-scores), list of
/// per-check results, regenerate / reshuffle / re-validate actions.
///
/// State model:
///   • Profile dropdown picks which profile to score
///   • OnNavigatedTo loads the list and scores the first profile
///   • Regenerate / Reshuffle write to DB and re-score
///   • History tab pulls from fingerprint_audits
/// </summary>
public sealed partial class FingerprintViewModel : BaseViewModel
{
    private readonly IFingerprintService _fp;
    private readonly IProfileService _profiles;
    private readonly IProfileRunner _runner;
    private readonly IDialogService _dialogs;
    private readonly IExternalTesterResultService _testerResults;
    private readonly ISelfCheckHistoryService _selfCheckHistory;
    private readonly ILogger<FingerprintViewModel> _log;

    private readonly Brush _okBrush;
    private readonly Brush _warnBrush;
    private readonly Brush _errBrush;
    private readonly Brush _dimBrush;

    public FingerprintViewModel(
        IFingerprintService fp,
        IProfileService profiles,
        IProfileRunner runner,
        IDialogService dialogs,
        IExternalTesterResultService testerResults,
        ISelfCheckHistoryService selfCheckHistory,
        ILogger<FingerprintViewModel> log)
    {
        _fp       = fp;
        _profiles = profiles;
        _runner   = runner;
        _dialogs  = dialogs;
        _testerResults = testerResults;
        _selfCheckHistory = selfCheckHistory;
        _log      = log;

        _okBrush   = (Brush)(Application.Current?.TryFindResource("OkBrush")    ?? Brushes.LimeGreen);
        _warnBrush = (Brush)(Application.Current?.TryFindResource("WarnBrush")  ?? Brushes.Orange);
        _errBrush  = (Brush)(Application.Current?.TryFindResource("ErrBrush")   ?? Brushes.IndianRed);
        _dimBrush  = (Brush)(Application.Current?.TryFindResource("TextDim")    ?? Brushes.Gray);

        ScoreBrush = _okBrush;

        // External testers — curated list. Order matches the legacy
        // web's display order (most-trusted first). Each tester is
        // checked by default; the "Probe in profile" command opens
        // every checked one in the launched browser.
        ExternalTesters = new ObservableCollection<ExternalTester>
        {
            new() { Name = "CreepJS",            Icon = "🧙",  Url = "https://abrahamjuliot.github.io/creepjs/",
                    Description = "Trust score 0-100, the strictest grader." },
            new() { Name = "Sannysoft Bot Test", Icon = "👹",  Url = "https://bot.sannysoft.com/",
                    Description = "The classic Selenium leak panel." },
            new() { Name = "Pixelscan",          Icon = "🍣",  Url = "https://pixelscan.net/fingerprint-check",
                    Description = "Canvas/WebGL hash uniqueness + geo correlation." },
            new() { Name = "AmIUnique",          Icon = "🔍",  Url = "https://amiunique.org/fingerprint",
                    Description = "Compares to a public fingerprint DB." },
            // Phase 32 — BrowserLeaks landing page is a nav directory,
            // not a probe. Point at /canvas which gives a real fingerprint
            // hash + signature in one page (most informative single
            // probe BrowserLeaks ships).
            new() { Name = "BrowserLeaks",       Icon = "💧",  Url = "https://browserleaks.com/canvas",
                    Description = "Canvas hash + signature uniqueness." },
            // Phase 58b — BotD now navigates to example.com (IANA-controlled
            // demo page that has zero CSP and zero JS, ideal blank canvas) and
            // loads the BotD UMD library from CDN. Earlier attempts:
            //   • fingerprint.com/products/bot-detection/ — has strict CSP
            //     `script-src 'self'`, blocks all CDNs.
            //   • about:blank — Chrome's opaque-origin policy blocks external
            //     <script src=https://...> on about: pages.
            // example.com is plain HTML with `Content-Security-Policy: default-src
            // 'unsafe-inline' 'unsafe-eval' 'self' data: https: ...` (permissive),
            // and is hosted by IANA, so it's effectively never down. We inject
            // BotD UMD via a <script> tag and call Botd.load().detect().
            new() { Name = "Fingerprint.com BotD",Icon = "🛡",  Url = "https://example.com/",
                    Description = "The realest test — commercial bot-detect (BotD library)." },
        };
    }

    /// <summary>
    /// External fingerprint testers (CreepJS, BrowserLeaks, etc.).
    /// Each tester's IsSelected drives whether "Probe in profile" opens it.
    /// </summary>
    public ObservableCollection<ExternalTester> ExternalTesters { get; }

    /// <summary>
    /// All device templates (catalog) for the left-side selector.
    /// Click a row to switch the focused profile's template_id and
    /// re-score immediately. Filtered by <see cref="TemplateFilter"/>.
    /// </summary>
    public ObservableCollection<DeviceTemplateRow> Templates { get; } = new();

    [ObservableProperty] private string _templateFilter = "all"; // all|desktop|laptop|phone

    partial void OnTemplateFilterChanged(string value) => RefreshTemplateList();

    public ObservableCollection<string> ProfileNames { get; } = new();

    [ObservableProperty] private string? _selectedProfile;

    /// <summary>
    /// Cancels the in-flight RefreshAsync when the user toggles the
    /// profile dropdown again before the previous score completes.
    /// Without this, two RefreshAsync calls could land out of order
    /// and the UI would briefly show stale data for the wrong profile.
    /// </summary>
    private CancellationTokenSource? _refreshCts;

    partial void OnSelectedProfileChanged(string? value)
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        _ = RefreshAsync(_refreshCts.Token);
        // Phase 31 — restore the per-tester probe verdicts persisted
        // on the previous probe-in-profile run so the cards aren't
        // blank when the user revisits the page.
        _ = RestoreTesterResultsAsync(value);
        // Load self-check test results from the latest self-check run
        _ = LoadSelfCheckHistoryAsync(value);
    }

    private async Task RestoreTesterResultsAsync(string? profileName)
    {
        // Reset every card first so a profile-switch clears stale verdicts.
        foreach (var t in ExternalTesters) { t.Result = null; t.Status = null; }
        if (string.IsNullOrEmpty(profileName)) return;
        try
        {
            var rows = await _testerResults.ListForProfileAsync(profileName);
            foreach (var t in ExternalTesters)
            {
                if (!rows.TryGetValue(t.Name, out var rec)) continue;
                IReadOnlyList<TesterDetailRow> details;
                try
                {
                    details = System.Text.Json.JsonSerializer
                        .Deserialize<List<TesterDetailRow>>(rec.DetailsJson)
                        ?? new List<TesterDetailRow>();
                }
                catch { details = Array.Empty<TesterDetailRow>(); }
                t.Result = new TesterResult
                {
                    TesterName = rec.TesterName,
                    Summary    = rec.Summary,
                    Verdict    = rec.Verdict,
                    Details    = details,
                    CapturedAt = rec.CapturedAt,
                };
                t.Status = "✓ " + rec.Summary;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't restore tester results for '{Profile}'", profileName);
        }
    }

    /// <summary>
    /// Load the latest self-check result for the profile and populate
    /// SelfCheckTests with per-probe cards. The score summary shows
    /// "X of N passed (Y%)" format.
    /// </summary>
    private async Task LoadSelfCheckHistoryAsync(string? profileName)
    {
        SelfCheckTests.Clear();
        SelfCheckScoreSummary = "";
        if (string.IsNullOrEmpty(profileName))
        {
            _log.LogInformation("LoadSelfCheckHistory: skipped (no profile selected)");
            return;
        }
        try
        {
            var latest = await _selfCheckHistory.GetLatestAsync(profileName);
            if (latest is null)
            {
                _log.LogInformation(
                    "LoadSelfCheckHistory: no rows in selfcheck_results for profile '{P}'",
                    profileName);
                return;
            }
            _log.LogInformation(
                "LoadSelfCheckHistory: loaded row #{Id} for '{P}' (ran_at={At}, tests_json_len={Len})",
                latest.Id, profileName, latest.RanAt,
                latest.TestsJson?.Length ?? 0);

            // Parse TestsJson into SelfCheckTestResult objects
            List<SelfCheckTestResult> tests = new();
            if (!string.IsNullOrEmpty(latest.TestsJson))
            {
                try
                {
                    tests = System.Text.Json.JsonSerializer
                        .Deserialize<List<SelfCheckTestResult>>(latest.TestsJson,
                            new System.Text.Json.JsonSerializerOptions
                            {
                                // The persisted JSON uses lowercase camelCase
                                // property names (default System.Text.Json
                                // PolicyDefault is PascalCase). Without
                                // PropertyNameCaseInsensitive, every required
                                // property fails to bind and Deserialize
                                // returns a list of empty rows OR throws.
                                PropertyNameCaseInsensitive = true,
                            })
                        ?? new();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "LoadSelfCheckHistory: failed to parse TestsJson for '{P}' (raw: {Snippet})",
                        profileName,
                        latest.TestsJson.Length > 200
                            ? latest.TestsJson[..200] + "…"
                            : latest.TestsJson);
                }
            }
            _log.LogInformation(
                "LoadSelfCheckHistory: parsed {N} test cards for '{P}'",
                tests.Count, profileName);

            // Populate the UI collection
            foreach (var test in tests)
            {
                // Short status badge shown on the compact card. Pre-
                // computed here (rather than via converter) so the XAML
                // stays declarative-only. Format examples:
                //   "PASS"
                //   "FAIL · 3840 ≠ 1920"
                //   "warn · 24-bit"
                //   "skip · no payload"
                string badge = test.Status switch
                {
                    "pass" => "PASS",
                    "fail" => string.IsNullOrEmpty(test.Actual)
                                ? "FAIL"
                                : $"FAIL · {Truncate(test.Actual, 18)}",
                    "warn" => string.IsNullOrEmpty(test.Actual)
                                ? "warn"
                                : $"warn · {Truncate(test.Actual, 18)}",
                    "skip" => "skip",
                    _      => test.Status,
                };

                // Tooltip — full multi-line breakdown so the user
                // doesn't need a separate detail dialog. Compact card
                // body shows label + badge; hover reveals everything
                // (expected, actual, detail, severity).
                var tipParts = new List<string>(4);
                if (!string.IsNullOrEmpty(test.Expected))
                    tipParts.Add($"Expected: {test.Expected}");
                if (!string.IsNullOrEmpty(test.Actual))
                    tipParts.Add($"Actual:   {test.Actual}");
                if (!string.IsNullOrEmpty(test.Detail))
                    tipParts.Add($"Detail:   {test.Detail}");
                tipParts.Add($"Severity: {test.Severity}  ·  Status: {test.Status}");

                SelfCheckTests.Add(new SelfCheckTestCardVm
                {
                    Label = test.Label,
                    Category = test.Category,
                    Status = test.Status,
                    Severity = test.Severity,
                    Expected = test.Expected,
                    Actual = test.Actual,
                    Detail = test.Detail,
                    StatusBadge = badge,
                    TooltipText = string.Join("\n", tipParts),
                    StatusColour = test.Status switch
                    {
                        "pass" => _okBrush,
                        "warn" => _warnBrush,
                        "fail" => _errBrush,
                        "skip" => _dimBrush,
                        _      => _dimBrush,
                    },
                    // Soft-tinted background that reads at a glance:
                    // pale green pass / amber warn / pale red fail.
                    // 15% opacity sits on top of the dark canvas
                    // without overwhelming the surrounding card grid.
                    StatusBgBrush = test.Status switch
                    {
                        "pass" => MakeTint(_okBrush,   0.14),
                        "warn" => MakeTint(_warnBrush, 0.16),
                        "fail" => MakeTint(_errBrush,  0.16),
                        "skip" => MakeTint(_dimBrush,  0.10),
                        _      => MakeTint(_dimBrush,  0.10),
                    },
                    CategoryIcon = test.Category switch
                    {
                        "navigator"   => "🌐",
                        "screen"      => "🖥",
                        "timezone"    => "🕐",
                        "webgl"       => "🎨",
                        "canvas"      => "🎨",
                        "audio"       => "🔊",
                        "fonts"       => "🔤",
                        "network"     => "📡",
                        "automation"  => "🤖",
                        _             => "📌",
                    },
                });
            }

            // Build score summary
            var passCount = tests.Count(t => t.Status == "pass");
            var totalCount = tests.Count;
            if (totalCount > 0)
            {
                SelfCheckScoreSummary = $"{passCount}/{totalCount} passed ({passCount * 100 / totalCount}%)";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't load self-check history for '{Profile}'", profileName);
        }
    }

    // ─── Score card ────────────────────────────────────────────
    [ObservableProperty] private int _overallScore;
    [ObservableProperty] private string _scoreLabel = "—";
    [ObservableProperty] private Brush  _scoreBrush;
    [ObservableProperty] private string _summaryLine = "";

    [ObservableProperty] private int _identityScore;
    [ObservableProperty] private int _hardwareScore;
    [ObservableProperty] private int _networkScore;
    [ObservableProperty] private int _automationScore;

    public ObservableCollection<CheckRowVm> Checks { get; } = new();

    [ObservableProperty] private int _passCount;
    [ObservableProperty] private int _warnCount;
    [ObservableProperty] private int _failCount;
    [ObservableProperty] private int _skipCount;

    /// <summary>
    /// Per-probe self-check test results (e.g. navigator.userAgent, screen.width).
    /// Populated when a profile is selected and self-check data is available.
    /// Each row is a SelfCheckTestCardVm showing expected vs actual values.
    /// </summary>
    public ObservableCollection<SelfCheckTestCardVm> SelfCheckTests { get; } = new();

    [ObservableProperty] private string _selfCheckScoreSummary = "";

    [ObservableProperty] private bool _isWorking;

    public override async Task OnNavigatedToAsync()
    {
        try
        {
            var list = await _profiles.ListAsync();
            ProfileNames.Clear();
            foreach (var p in list) ProfileNames.Add(p.Name);
            if (ProfileNames.Count > 0 && SelectedProfile is null)
            {
                // Setting SelectedProfile triggers OnSelectedProfileChanged
                // which fires the full reload chain (RefreshAsync +
                // RestoreTesterResultsAsync + LoadSelfCheckHistoryAsync).
                SelectedProfile = ProfileNames[0];
            }
            else
            {
                // SelectedProfile was already set (e.g. user navigated
                // away then came back). OnSelectedProfileChanged WON'T
                // fire — same-value-set is a no-op for [ObservableProperty].
                // We still need to refresh BOTH the FP score AND the
                // self-check cards because the user may have launched
                // the profile in the meantime and a new self-check row
                // landed in the DB. Without this LoadSelfCheckHistory
                // call, the page stays stale ("No self-check data yet")
                // forever despite fresh rows being persisted on every
                // launch.
                await RefreshAsync();
                _ = LoadSelfCheckHistoryAsync(SelectedProfile);
            }
            RefreshTemplateList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Fingerprint page initial load failed");
        }
    }

    private void RefreshTemplateList()
    {
        // The catalog is static; filter & rebuild on every nav and on
        // template-filter chip changes. Total weight is computed across
        // the FILTERED set so the percentages add up to 100% within
        // the chosen filter.
        var filter = TemplateFilter ?? "all";
        var all = DeviceTemplateCatalog.All;
        IEnumerable<DeviceTemplate> filtered = filter switch
        {
            "desktop" => all.Where(t => t.FormFactor == FormFactor.Desktop && !t.IsLaptop),
            "laptop"  => all.Where(t => t.IsLaptop),
            "phone"   => all.Where(t => t.FormFactor == FormFactor.Mobile),
            _         => all,
        };
        var ordered = filtered
            .OrderByDescending(t => t.Weight)
            .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var totalWeight = Math.Max(1, ordered.Sum(t => t.Weight));
        Templates.Clear();
        foreach (var t in ordered)
        {
            var pct = 100.0 * t.Weight / totalWeight;
            // Phase 53 — compute both string and numeric versions so the UI
            // can color-code by percentage (≥5% amber, 2-5% teal, <2% grey).
            Templates.Add(new DeviceTemplateRow
            {
                Id          = t.Id,
                Label       = t.ToLabel(),
                FormFactor  = t.FormFactor.ToString().ToLowerInvariant(),
                IsLaptop    = t.IsLaptop,
                WeightPct   = $"{pct:F1}%",
                WeightPctNum = pct,
                // Coarse tier bucket — XAML DataTrigger needs exact-
                // equality match, so we pre-compute "top"/"med"/"low"
                // here instead of expecting the trigger to do range
                // arithmetic. Threshold mirrors the design intent:
                // ≥5% = bright amber accent, 2-5% = teal, rest grey.
                WeightTier  = pct >= 5.0 ? "top"
                              : pct >= 2.0 ? "med"
                              : "low",
            });
        }
    }

    [RelayCommand]
    private async Task SelectTemplateAsync(DeviceTemplateRow? row)
    {
        if (row is null || string.IsNullOrEmpty(SelectedProfile)) return;
        try
        {
            var p = await _profiles.GetAsync(SelectedProfile)
                ?? throw new InvalidOperationException("Profile gone");
            // We only update the TemplateId; everything else stays the
            // same. UpdateAsync persists; then we re-score so the user
            // sees the consequences of the swap immediately.
            var updated = new Profile
            {
                Name             = p.Name,
                GroupName        = p.GroupName,
                TemplateId       = row.Id,
                Language         = p.Language,
                ProxySlug        = p.ProxySlug,
                IsReady          = p.IsReady,
                EnrichOnFirstRun = p.EnrichOnFirstRun,
                LastRunAt        = p.LastRunAt,
                RunCount         = p.RunCount,
                Note             = p.Note,
                CreatedAt        = p.CreatedAt,
                UpdatedAt        = DateTime.UtcNow,
                FpRegenSalt      = p.FpRegenSalt,
                FpNoiseSalt      = p.FpNoiseSalt,
            };
            await _profiles.UpdateAsync(updated);
            _log.LogInformation("Template switched to {TemplateId} for profile '{Profile}'", row.Id, SelectedProfile);
            // Phase 53 — refresh the score immediately and show success toast.
            await RefreshAsync();
            await _dialogs.ConfirmAsync(
                "Template applied",
                $"Switched to {row.Id}. Score updated.",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Switch template failed");
            await _dialogs.ConfirmAsync("Switch failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(SelectedProfile)) return;
        var snapshot = SelectedProfile; // capture so a mid-flight switch doesn't apply to wrong profile
        IsBusy = true;
        try
        {
            var score = await _fp.GetScoreAsync(snapshot, ct);
            // If the user switched profiles while we awaited, drop
            // the stale result on the floor.
            if (ct.IsCancellationRequested) return;
            if (!string.Equals(snapshot, SelectedProfile, StringComparison.Ordinal)) return;
            ApplyScore(score);
        }
        catch (OperationCanceledException) { /* expected on profile switch */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "GetScore failed for '{Profile}'", snapshot);
            ScoreLabel = "ERROR";
            ScoreBrush = _errBrush;
            SummaryLine = ex.Message;
            Checks.Clear();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RegenerateAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile) || IsWorking) return;
        IsWorking = true;
        try
        {
            var score = await _fp.RegenerateAsync(SelectedProfile);
            ApplyScore(score);
            await _dialogs.ConfirmAsync(
                "Fingerprint regenerated",
                $"New score: {score.Overall}/100 ({score.Label}). " +
                "Next browser launch uses the fresh payload.",
                "OK", ConfirmSeverity.Success);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Regenerate failed");
            await _dialogs.ConfirmAsync("Regenerate failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsWorking = false; }
    }

    [RelayCommand]
    private async Task ReshuffleAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile) || IsWorking) return;
        IsWorking = true;
        try
        {
            var score = await _fp.ReshuffleAsync(SelectedProfile);
            ApplyScore(score);
            await _dialogs.ConfirmAsync(
                "Noise re-rolled",
                $"Canvas/WebGL/audio jitter rolled. Score: {score.Overall}/100.",
                "OK", ConfirmSeverity.Info);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Reshuffle failed");
            await _dialogs.ConfirmAsync("Reshuffle failed", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally { IsWorking = false; }
    }

    /// <summary>Phase 31 — open the per-tester detail dialog for a
    /// card the user clicked. No-op when the card has no result yet.</summary>
    [RelayCommand]
    private async Task ShowTesterDetailsAsync(ExternalTester? t)
    {
        if (t?.Result is null) return;
        var r = t.Result;
        var lines = string.Join("\n",
            r.Details.Select(d => $"  • {d.Key}: {d.Value}"));
        var body = $"Verdict: {r.Verdict}\nSummary: {r.Summary}\nCaptured: {r.CapturedAt:yyyy-MM-dd HH:mm:ss} UTC\n\n{lines}";
        var sev = r.Verdict switch
        {
            "excellent" => ConfirmSeverity.Success,
            "ok"        => ConfirmSeverity.Success,
            "weak"      => ConfirmSeverity.Warning,
            "flagged"   => ConfirmSeverity.Error,
            _           => ConfirmSeverity.Info,
        };
        await _dialogs.ConfirmAsync($"{t.Name} — probe result", body, "OK", sev);
    }

    /// <summary>Phase 35 — open the self-check test card detail dialog showing
    /// Expected vs Actual values, status, severity, category, and for FAIL/WARN
    /// cases, a "Possible cause" explanation paragraph based on the category.</summary>
    public async Task ShowSelfCheckDetailAsync(SelfCheckTestCardVm card)
    {
        // Build detail lines with all relevant information.
        // Include status, severity, category, expected/actual values, and detail text.
        var lines = new List<string>
        {
            $"Status:   {card.Status.ToUpper()}",
            $"Severity: {card.Severity}",
            $"Category: {card.Category}",
            ""
        };

        if (!string.IsNullOrEmpty(card.Expected))
            lines.Add($"Expected: {card.Expected}");
        if (!string.IsNullOrEmpty(card.Actual))
            lines.Add($"Actual:   {card.Actual}");
        if (!string.IsNullOrEmpty(card.Detail))
            lines.Add($"Detail:   {card.Detail}");

        // For FAIL or WARN status, add a "Possible cause" section that explains
        // what could have gone wrong based on the category. This gives users
        // a starting point for debugging without being exhaustive (not all
        // causes are covered — just the most common ones per category).
        if (card.Status == "fail" || card.Status == "warn")
        {
            lines.Add("");
            lines.Add("Possible cause:");
            lines.Add(GuessFailureCause(card.Category, card.Label));
        }

        // Determine severity for the dialog's visual style based on the card's status.
        var severity = card.Status switch
        {
            "fail" => ConfirmSeverity.Error,
            "warn" => ConfirmSeverity.Warning,
            "pass" => ConfirmSeverity.Success,
            _      => ConfirmSeverity.Info,
        };

        await _dialogs.ConfirmAsync(
            $"{card.Label} — self-check probe",
            string.Join("\n", lines),
            "OK",
            severity);
    }

    /// <summary>Phase 35 — command wired from self-check card click. Calls
    /// ShowSelfCheckDetailAsync to open the detail dialog.</summary>
    [RelayCommand]
    private async Task ShowSelfCheckDetail(SelfCheckTestCardVm? card)
    {
        if (card is null) return;
        await ShowSelfCheckDetailAsync(card);
    }

    /// <summary>Helper to provide user-friendly explanation of why a self-check
    /// test might have failed, based on the category and label. Returns a 1-2
    /// sentence hint that guides the user toward the likely root cause.</summary>
    private static string GuessFailureCause(string category, string label) => category switch
    {
        "navigator"   => "navigator.* property override didn't reach JS — Chromium patch missing or payload key mismatch.",
        "screen"      => "screen.* override didn't apply — check ghost_shell_browser/../screen.cc patch is built into chrome.exe.",
        "timezone"    => "Intl.DateTimeFormat() returns system TZ — Chromium ICU::TimeZone::adoptDefault patch may not be in this build.",
        "webgl"       => "WEBGL_debug_renderer_info getParameter not patched — check graphics_context.cc / webgl extension override.",
        "canvas"      => "Canvas fingerprint unavailable or blocked — check that canvas.getContext('2d') is working correctly.",
        "audio"       => "AudioContext.sampleRate jitter mismatch — within ±1 Hz it's expected (intentional noise), >1 means patch mis-set.",
        "automation"  => "navigator.webdriver leaked — patched_navigator.cc must zero this property.",
        _             => "Patch missing or payload key mismatch — check c++ override file for this property.",
    };

    /// <summary>
    /// Launch the patched browser for the focused profile and copy
    /// every selected tester URL into the clipboard so the user can
    /// paste them as new tabs. We don't auto-navigate yet — that
    /// requires CDP automation per-tab and is on the Phase 11 roadmap.
    /// For v1 the goal is "click → browser opens with the right
    /// fingerprint payload, here are the URLs you wanted".
    /// </summary>
    [RelayCommand]
    private async Task ProbeInProfileAsync()
    {
        if (string.IsNullOrEmpty(SelectedProfile))
        {
            await _dialogs.ConfirmAsync(
                "No profile selected",
                "Pick a profile in the dropdown above first.",
                "OK", ConfirmSeverity.Warning);
            return;
        }
        var selected = ExternalTesters.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0)
        {
            await _dialogs.ConfirmAsync(
                "No testers selected",
                "Tick at least one tester card before probing.",
                "OK", ConfirmSeverity.Warning);
            return;
        }

        // Reset the per-card status so the user sees fresh state on
        // each Probe click.
        foreach (var t in selected) t.Status = "queued";

        // Track whether this invocation launched the browser so we can
        // clean it up afterward. If the user had the profile running
        // before the probe, we leave it open (they may want it for their
        // own work). If we started it, we stop it when the probe completes.
        bool weStartedTheBrowser = false;

        try
        {
            // Start the profile if it isn't already running. We pass
            // runAssignedScript:false so the user's GoodMedika / ad-
            // click automation does NOT side-effect this probe — the
            // browser launches for tester navigation only. Without
            // this, the script would race the tester URLs, navigate
            // away from CreepJS / Sannysoft mid-probe, and count its
            // ad clicks against the user's CTR analytics.
            if (!_runner.ActiveProfileNames.Contains(SelectedProfile))
            {
                var profile = await _profiles.GetAsync(SelectedProfile)
                    ?? throw new InvalidOperationException($"Profile '{SelectedProfile}' not found");
                _ = await _runner.StartAsync(profile, ct: default, runAssignedScript: false);
                weStartedTheBrowser = true;
                // Give the chromedriver about:blank navigate + the
                // self-check probe (3s after launch) breathing room
                // before we hijack the session.
                await Task.Delay(TimeSpan.FromSeconds(4));
            }

            var session = _runner.TryGetActiveSession(SelectedProfile);
            if (session is null)
            {
                await _dialogs.ConfirmAsync(
                    "Session unavailable",
                    "The browser session isn't ready yet (still starting up or mid-teardown). Try again in a few seconds.",
                    "OK", ConfirmSeverity.Warning);
                return;
            }

            // Walk through each tester sequentially. We DON'T spawn
            // tabs in parallel — Chrome assigns the same window so a
            // background tab's JS still runs but the user can't watch
            // progress. Sequential keeps the active tab on whatever
            // probe just finished.
            bool browserClosedMidProbe = false;
            for (int i = 0; i < selected.Count; i++)
            {
                var t = selected[i];
                t.Status = "navigating…";
                try
                {
                    // Phase 33 — before every tester, do a quick session-liveness
                    // check. If the user closed the browser externally, we'll
                    // detect it here instead of getting ObjectDisposedException
                    // from the extractor. This prevents confusing errors when
                    // users close the browser mid-probe.
                    if (!await TesterProbe.IsSessionAlive(session))
                    {
                        t.Status = "✗ browser closed";
                        browserClosedMidProbe = true;
                        break;
                    }

                    // Phase 34 — Sannysoft (and any other testers marked SkipNavigationFor)
                    // run inline JS checks without needing a page load. Skip the navigate
                    // for those testers; they'll run their extraction on whatever page
                    // the browser is currently on (typically about:blank after startup).
                    if (!TesterProbe.SkipNavigationFor(t.Name))
                    {
                        await session.NavigateAsync(t.Url);
                    }

                    // Phase 33 — poll-and-extract: instead of a single fixed
                    // delay + one extraction, we now ask "is the verdict ready?"
                    // repeatedly. Most sites finish in 3-6s on decent networks;
                    // slower networks get up to 30s for CreepJS. The user sees
                    // a countdown so they know we're still waiting.
                    var settle = TesterProbe.SettleFor(t.Name);      // 3s for all
                    var extractTimeout = TesterProbe.MaxWaitFor(t.Name);
                    var pollInterval = TimeSpan.FromSeconds(2);
                    TesterResult? detailedResult = null;

                    // Initial settle — give the page time to start loading
                    t.Status = $"loading ({settle.TotalSeconds:0}s)…";
                    await Task.Delay(settle);

                    // Poll-and-extract loop — keep trying until verdict appears
                    // or we hit the deadline. Each iteration asks "is the result
                    // ready?" via ExtractDetailedAsync. If the extractor returns
                    // a "?" verdict, it means the page didn't render the answer yet.
                    var deadline = DateTime.UtcNow.Add(extractTimeout);
                    while (DateTime.UtcNow < deadline)
                    {
                        try
                        {
                            detailedResult = await TesterProbe.ExtractDetailedAsync(t.Name, session);

                            // Got a real verdict? (anything other than "?")
                            if (!string.IsNullOrEmpty(detailedResult.Verdict) && detailedResult.Verdict != "?")
                            {
                                break; // Verdict ready — exit the poll loop
                            }

                            // Verdict not ready yet. Show countdown and wait.
                            var secondsLeft = (deadline - DateTime.UtcNow).TotalSeconds;
                            t.Status = $"polling… ({secondsLeft:0}s left)";
                            await Task.Delay(pollInterval);

                            // Re-check session liveness inside the poll loop —
                            // the user can close the browser at any time during
                            // the wait. If they do, break cleanly.
                            if (!await TesterProbe.IsSessionAlive(session))
                            {
                                t.Status = "✗ browser closed";
                                browserClosedMidProbe = true;
                                break;
                            }
                        }
                        catch (ObjectDisposedException)
                        {
                            // Browser was closed / session disposed externally
                            t.Status = "✗ browser closed";
                            browserClosedMidProbe = true;
                            break;
                        }
                        catch (Exception ex) when (ex.Message.Contains("session", StringComparison.OrdinalIgnoreCase)
                                                || ex.Message.Contains("disposed", StringComparison.OrdinalIgnoreCase))
                        {
                            // Session-level error (e.g. "could not find session")
                            t.Status = "✗ browser closed";
                            browserClosedMidProbe = true;
                            break;
                        }
                    }

                    // If we broke out due to browser closure, stop the loop
                    if (browserClosedMidProbe) break;

                    // Extract phase is complete (either we got a verdict or
                    // the deadline passed). Prepare the summary for the card.
                    string summary = "";
                    if (detailedResult?.Verdict != "?" && !string.IsNullOrEmpty(detailedResult?.Summary))
                    {
                        summary = detailedResult.Summary;
                    }
                    else
                    {
                        // Phase 34 — If polling timed out and we never got a real
                        // verdict, show a clearer message explaining the page didn't
                        // finish processing, rather than showing the last poll status
                        // "still computing" which is misleading (we've moved past that point).
                        var totalWaitSeconds = (int)extractTimeout.TotalSeconds;
                        summary = $"✗ no result after {totalWaitSeconds}s (page may not have processed)";
                    }

                    t.Status = "✓ " + summary;
                    t.Result = detailedResult;

                    // Phase 31 — persist so the card restores its
                    // verdict on next page open. Best-effort; a DB
                    // hiccup shouldn't fail the probe loop.
                    if (detailedResult is not null)
                    {
                        try
                        {
                            var detailsJson = System.Text.Json.JsonSerializer.Serialize(detailedResult.Details);
                            await _testerResults.UpsertAsync(
                                SelectedProfile!, t.Name,
                                detailedResult.Summary ?? "",
                                detailedResult.Verdict ?? "",
                                detailsJson,
                                detailedResult.CapturedAt);
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex,
                                "Couldn't persist tester result for '{Profile}'/{Tester}",
                                SelectedProfile, t.Name);
                        }
                    }

                    _log.LogInformation(
                        "External tester '{Name}' probe complete for '{Profile}': {Result}",
                        t.Name, SelectedProfile, summary);
                }
                catch (OperationCanceledException) { t.Status = "✗ cancelled"; throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex,
                        "External tester probe '{Name}' failed for profile '{Profile}'",
                        t.Name, SelectedProfile);
                    t.Status = "✗ " + (ex.Message.Length > 60
                        ? ex.Message[..60] + "…" : ex.Message);
                }
            }

            // Phase 33 — if the browser closed mid-probe, inform the user
            // that we skipped remaining testers.
            if (browserClosedMidProbe)
            {
                await _dialogs.ConfirmAsync(
                    "Browser closed mid-probe",
                    "The browser was closed externally. Remaining testers were skipped.",
                    "OK", ConfirmSeverity.Warning);
            }
            // Phase 31 — no completion dialog. Status hides on each
            // card, the user reads the result either inline or by
            // looking at the live browser window.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Probe-in-profile failed");
            await _dialogs.ConfirmAsync(
                "Could not run probe", ex.Message,
                "OK", ConfirmSeverity.Error);
        }
        finally
        {
            // Phase 34 — Close the browser if WE launched it for the probe.
            // If the user had it running before, leave it open since they
            // may want to continue using it for their own work. This
            // prevents browser windows from lingering after a probe completes.
            if (weStartedTheBrowser && !string.IsNullOrEmpty(SelectedProfile))
            {
                try
                {
                    _log.LogInformation("Probe complete; closing browser (we launched it for the probe)");
                    await _runner.StopAsync(SelectedProfile);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Couldn't cleanly close the browser after probe");
                }
            }

            // Phase 57 — Auto-refresh the Fingerprint page UI now that the probe
            // is done. The probe drives a fresh self-check run + writes a new row
            // to selfcheck_results; without this the user sees the OLD card grid
            // (row #N-1) and thinks the probe didn't update anything. Reload the
            // history (new card grid) AND re-score (refreshes coherence checks
            // since some can change after a real browser launch). Best-effort —
            // a load failure shouldn't surface as a probe failure.
            if (!string.IsNullOrEmpty(SelectedProfile))
            {
                try
                {
                    // Slight delay so the SelfCheckService row hits the DB before
                    // we read it (the probe runs the self-check via Bootstrap
                    // ScheduleAsync which is fire-and-forget).
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    await LoadSelfCheckHistoryAsync(SelectedProfile);
                    _refreshCts?.Cancel();
                    _refreshCts?.Dispose();
                    _refreshCts = new CancellationTokenSource();
                    _ = RefreshAsync(_refreshCts.Token);
                    _log.LogInformation(
                        "Fingerprint page auto-refreshed after probe for '{P}'",
                        SelectedProfile);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Couldn't auto-refresh Fingerprint page after probe");
                }
            }
        }
    }

    /// <summary>
    /// Build a translucent SolidColorBrush from another brush at the
    /// given opacity (0..1). Used for the self-check card backgrounds:
    /// status colour at low opacity gives a soft tint over the dark
    /// canvas so cards read as "this is the result class" without an
    /// outlined border. Falls back to a flat grey if the source brush
    /// isn't a SolidColorBrush (e.g. theme-resourced gradient).
    /// </summary>
    private static Brush MakeTint(Brush source, double opacity)
    {
        if (source is SolidColorBrush scb)
        {
            var c = scb.Color;
            // Scale alpha — 0.14 opacity over a dark surface paints
            // a barely-there wash. Multiply by source alpha so a
            // half-transparent source doesn't get accidentally boosted.
            byte alpha = (byte)Math.Clamp(c.A * opacity, 0, 255);
            return new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
        }
        return new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(255 * opacity), 128, 128, 128));
    }

    /// <summary>
    /// Cap a string at <paramref name="max"/> visible characters,
    /// appending an ellipsis (… counts as one char). Returns "" for
    /// null input. Used by the self-check card badges so the inline
    /// "FAIL · expected…" text doesn't overflow the 210px card width.
    /// </summary>
    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Length <= max) return s;
        return s[..(max - 1)] + "…";
    }

    private void ApplyScore(FingerprintScore s)
    {
        OverallScore     = s.Overall;
        ScoreLabel       = s.Label;
        ScoreBrush       = s.Overall >= 85 ? _okBrush
                         : s.Overall >= 75 ? _warnBrush
                         : _errBrush;
        SummaryLine      = $"{s.Overall}/100 — {s.CriticalIssues} critical, {s.Warnings} warnings";
        IdentityScore    = s.Identity;
        HardwareScore    = s.Hardware;
        NetworkScore     = s.Network;
        AutomationScore  = s.Automation;

        Checks.Clear();
        foreach (var c in s.Checks)
            Checks.Add(CheckRowVm.From(c, _okBrush, _warnBrush, _errBrush, _dimBrush));

        PassCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Pass);
        WarnCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Warn);
        FailCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Fail);
        SkipCount = s.Checks.Count(c => c.Status == FingerprintCheckStatus.Skip);
    }
}

/// <summary>
/// Row in the Device-templates panel on the Fingerprint page.
/// Click → switches the focused profile's template_id.
/// </summary>
public sealed record DeviceTemplateRow
{
    public required string Id          { get; init; }
    public required string Label       { get; init; }
    public required string FormFactor  { get; init; }
    public required bool   IsLaptop    { get; init; }
    public required string WeightPct   { get; init; }
    /// <summary>Numeric version of WeightPct (8.6 from "8.6%"). Kept
    /// for sorts / future numeric DataTriggers, not used by the
    /// current UI (which binds to <see cref="WeightTier"/> instead
    /// because WPF's DataTrigger only does exact-equality match).</summary>
    public required double WeightPctNum { get; init; }
    /// <summary>
    /// Pre-computed coarse bucket — "top" (≥5%), "med" (2-5%), or
    /// "low" (&lt;2%). The XAML weight-badge style triggers on this
    /// string instead of the raw double because WPF's DataTrigger
    /// can't express ">=" / "between"; trying that with numeric
    /// values silently never matched (Phase 54 fix). Filled in
    /// alongside WeightPctNum during template-list refresh.
    /// </summary>
    public required string WeightTier { get; init; }
}

/// <summary>
/// One external fingerprint-tester entry on the Fingerprint page.
/// IsSelected is mutable (drives the "Probe in profile" target set).
/// </summary>
public sealed partial class ExternalTester : ObservableObject
{
    public required string Name        { get; init; }
    public required string Icon        { get; init; }
    public required string Url         { get; init; }
    public required string Description { get; init; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _resultText = "no result yet — click 🚀 below";

    /// <summary>Phase 29 — live probe status surfaced on the card
    /// while the automated probe walks through testers. Values shift
    /// through "queued" → "navigating…" → "running (Ns)…" → "✓ {title}"
    /// or "✗ {error}".</summary>
    [ObservableProperty] private string? _status;

    /// <summary>Phase 31 — full extracted result. Populated by the
    /// site-specific extractor; null until the user clicks Probe.</summary>
    [ObservableProperty] private TesterResult? _result;

    /// <summary>True when there's a result to show in the detail
    /// dialog; drives the "click for details" affordance on the card.</summary>
    public bool HasResult => Result is not null;

    /// <summary>"excellent" | "ok" | "weak" | "flagged" | "info" |
    /// "?" — used by the XAML DataTrigger to colour the status pill.</summary>
    public string VerdictKey => Result?.Verdict ?? "";

    // Source generator only allows one OnXChanged overload per
    // observable property, so fan out to the dependent props from
    // a single handler.
    partial void OnResultChanged(TesterResult? value)
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(VerdictKey));
    }
}

public sealed record CheckRowVm
{
    public required string Title  { get; init; }
    public required string Detail { get; init; }
    public required string StatusText { get; init; }
    public required Brush  StatusBrush { get; init; }
    public required string SeverityLabel { get; init; }

    public static CheckRowVm From(FingerprintCheck c, Brush ok, Brush warn, Brush err, Brush dim) => new()
    {
        Title = c.Title,
        Detail = c.Detail,
        StatusText = c.Status switch
        {
            FingerprintCheckStatus.Pass => "PASS",
            FingerprintCheckStatus.Warn => "WARN",
            FingerprintCheckStatus.Fail => "FAIL",
            _                           => "SKIP",
        },
        StatusBrush = c.Status switch
        {
            FingerprintCheckStatus.Pass => ok,
            FingerprintCheckStatus.Warn => warn,
            FingerprintCheckStatus.Fail => err,
            _                           => dim,
        },
        SeverityLabel = c.Severity switch
        {
            FingerprintCheckSeverity.Critical => "CRITICAL",
            FingerprintCheckSeverity.Warning  => "WARNING",
            _                                 => "INFO",
        },
    };
}

/// <summary>
/// A single self-check test result rendered as a card on the
/// Runtime Self-Check section. Each row shows expected vs actual
/// values, status, category icon, and severity.
/// </summary>
public sealed record SelfCheckTestCardVm
{
    /// <summary>Human-readable test label (e.g. "User-Agent", "Screen Width").</summary>
    public required string Label { get; init; }

    /// <summary>
    /// Category grouping: "navigator", "screen", "timezone", "webgl",
    /// "canvas", "audio", "fonts", "network", "automation".
    /// </summary>
    public required string Category { get; init; }

    /// <summary>Test status: "pass", "warn", "fail", or "skip".</summary>
    public required string Status { get; init; }

    /// <summary>Severity level: "critical", "important", "warning", "info".</summary>
    public required string Severity { get; init; }

    /// <summary>Expected value from the fingerprint payload.</summary>
    public string? Expected { get; init; }

    /// <summary>Actual value from the live JS probe.</summary>
    public string? Actual { get; init; }

    /// <summary>Extra detail when status != "pass".</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Brush colour based on Status: Green for pass, Orange for warn,
    /// Red for fail, Gray for skip. Used to colour the status pill
    /// + status-badge text.
    /// </summary>
    public required Brush StatusColour { get; init; }

    /// <summary>
    /// Soft translucent fill of the status colour (~14-16% opacity)
    /// painted as the card background. Lets the user scan the grid
    /// for fails (red wash) / warns (amber wash) without needing a
    /// border accent.
    /// </summary>
    public required Brush StatusBgBrush { get; init; }

    /// <summary>
    /// Category emoji: 🌐 navigator, 🖥 screen, 🕐 timezone, 🎨 webgl/canvas,
    /// 🔊 audio, 🔤 fonts, 📡 network, 🤖 automation.
    /// </summary>
    public required string CategoryIcon { get; init; }

    /// <summary>
    /// Short coloured badge text shown at the bottom of the compact
    /// card ("PASS", "FAIL · 3840 ≠ 1920", "warn · 24-bit", "skip").
    /// Pre-computed in the loader so the XAML stays declarative.
    /// </summary>
    public required string StatusBadge { get; init; }

    /// <summary>
    /// Multi-line tooltip shown on card hover. Spells out Expected,
    /// Actual, Detail and Severity so the small card body can stay
    /// uncluttered (label + badge only). Joined with newlines —
    /// WPF's ToolTip wraps at \n.
    /// </summary>
    public required string TooltipText { get; init; }
}

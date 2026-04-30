// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GhostShell.App.Logging;
using GhostShell.Core.Common;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.ViewModels;

public sealed partial class SettingsViewModel : BaseViewModel
{
    private readonly IChromiumLocator _chromiumLocator;
    private readonly ILogger<SettingsViewModel> _log;

    public SettingsViewModel(IChromiumLocator chromiumLocator, ILogger<SettingsViewModel> log)
    {
        _chromiumLocator = chromiumLocator;
        _log             = log;
        ProbeChromium();
    }

    [ObservableProperty] private string _dataDirectory   = AppPaths.DataDir;
    [ObservableProperty] private string _databasePath    = AppPaths.DatabasePath;
    [ObservableProperty] private string _logsDirectory   = AppPaths.LogsDir;
    [ObservableProperty] private string _currentLogFile  = LoggingSetup.CurrentLogPath;

    // ─── Chromium status ──────────────────────────────────────────
    [ObservableProperty] private string _chromiumPath          = "—";
    [ObservableProperty] private string _chromedriverPath      = "—";
    [ObservableProperty] private string _chromiumVersion       = "—";
    [ObservableProperty] private string _chromiumProbedFrom    = "—";
    [ObservableProperty] private string _chromiumStatusMessage = "Probing…";
    [ObservableProperty] private bool   _chromiumFound;
    [ObservableProperty] private string _chromiumCandidates    = "";

    [ObservableProperty]
    private string _appVersion =
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    [RelayCommand]
    private void ProbeChromium()
    {
        try
        {
            var status = _chromiumLocator.Locate();
            ChromiumFound          = status.Found;
            ChromiumPath           = status.ChromePath       ?? "—";
            ChromedriverPath       = status.ChromeDriverPath ?? "—";
            ChromiumVersion        = status.VersionString    ?? "—";
            ChromiumProbedFrom     = status.ProbedFrom       ?? "—";
            ChromiumStatusMessage  = status.Found
                ? $"Located via {status.ProbedFrom}"
                : status.Error ?? "Not found.";
            ChromiumCandidates     = string.Join("\n", status.Candidates);
            _log.LogInformation(
                "Chromium probe: found={Found}, path='{Path}', version={Ver}",
                status.Found, ChromiumPath, ChromiumVersion);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Chromium probe threw");
            ChromiumFound         = false;
            ChromiumStatusMessage = $"Probe failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        TryOpenInExplorer(AppPaths.LogsDir, "logs folder");
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        TryOpenInExplorer(AppPaths.DataDir, "data folder");
    }

    [RelayCommand]
    private void OpenChromiumFolder()
    {
        if (string.IsNullOrEmpty(ChromiumPath) || ChromiumPath == "—") return;
        var dir = Path.GetDirectoryName(ChromiumPath);
        if (!string.IsNullOrEmpty(dir)) TryOpenInExplorer(dir, "Chromium folder");
    }

    private void TryOpenInExplorer(string path, string label)
    {
        try
        {
            _log.LogInformation("Opening {Label} in Explorer: {Path}", label, path);
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to open {Label}", label);
        }
    }
}

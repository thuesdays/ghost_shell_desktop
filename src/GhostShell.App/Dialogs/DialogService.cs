// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Windows;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Real implementation. Owns the WPF Window lifetime so VMs stay
/// shell-agnostic. Each method opens a modal `Window`, waits for the
/// user, returns the produced entity (or null on cancel).
///
/// Note on threading: WPF dialog instantiation must happen on the UI
/// thread. The methods use Dispatcher.Invoke to be safe even when
/// called from a background-thread continuation (e.g. after `await`
/// on an SQL query that completes off-thread).
/// </summary>
internal sealed class DialogService : IDialogService
{
    private readonly IProxyService _proxies;
    private readonly IProfileService _profiles;
    private readonly IProfileGroupService _groups;
    private readonly IScheduleService _schedules;
    private readonly ILogger<DialogService> _log;

    public DialogService(
        IProxyService proxies,
        IProfileService profiles,
        IProfileGroupService groups,
        IScheduleService schedules,
        ILogger<DialogService> log)
    {
        _proxies   = proxies;
        _profiles  = profiles;
        _groups    = groups;
        _schedules = schedules;
        _log       = log;
    }

    public async Task<Profile?> ShowProfileEditorAsync(Profile? existing = null)
    {
        // Pre-load proxies off-thread and pre-format them into
        // ProxyOption so the dialog combo can render two-line entries
        // (display name + sub-line with country / IP type / latency)
        // without doing service lookups during template binding.
        var options = (await _proxies.ListAsync())
            .Select(ProxyOption.FromProxy)
            .ToList();

        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new ProfileEditorDialog(existing, options)
            {
                Owner = Application.Current.MainWindow,
            };
            var ok = dlg.ShowDialog() == true;
            _log.LogDebug("ProfileEditor closed: ok={Ok}, existing={ExistingName}",
                ok, existing?.Name ?? "—");
            return ok ? dlg.Result : null;
        });
    }

    public Task<Proxy?> ShowProxyEditorAsync(
        Proxy? existing = null,
        Func<string, string, Task<string?>>? onRotateNow = null)
    {
        var result = Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new ProxyEditorDialog(existing)
            {
                Owner             = Application.Current.MainWindow,
                OnRotateRequested = onRotateNow,
            };
            var ok = dlg.ShowDialog() == true;
            _log.LogDebug("ProxyEditor closed: ok={Ok}, existing={ExistingSlug}",
                ok, existing?.Slug ?? "—");
            return ok ? dlg.Result : null;
        });
        return Task.FromResult(result);
    }

    public Task<bool> ShowGroupEditorAsync(
        ProfileGroup? existing,
        IReadOnlyList<Profile> allProfiles)
    {
        var result = Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new GroupEditorDialog(_groups, existing, allProfiles)
            {
                Owner = Application.Current.MainWindow,
            };
            dlg.ShowDialog();
            return dlg.DidSave;
        });
        _log.LogDebug("GroupEditor closed: didSave={Result}, edit={Existing}",
            result, existing?.Id);
        return Task.FromResult(result);
    }

    public Task<bool> ShowScheduleEditorAsync(
        Schedule? existing,
        IReadOnlyList<string> profileNames,
        IReadOnlyList<string> groupNames)
    {
        var result = Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new ScheduleEditorDialog(_schedules, existing, profileNames, groupNames)
            {
                Owner = Application.Current.MainWindow,
            };
            dlg.ShowDialog();
            return dlg.DidSave;
        });
        _log.LogDebug("ScheduleEditor closed: didSave={Result}, edit={Existing}",
            result, existing?.Id);
        return Task.FromResult(result);
    }

    public Task<bool> ShowBulkCreateProfilesAsync()
    {
        // Dialog handles the create itself (so progress streams inline)
        // — we just hand it the services and report the boolean back.
        var result = Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new BulkCreateProfilesDialog(_profiles, _proxies)
            {
                Owner = Application.Current.MainWindow,
            };
            dlg.ShowDialog();
            return dlg.DidCreate;
        });
        _log.LogDebug("BulkCreateProfiles closed: didCreate={Result}", result);
        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<ParsedProxy>?> ShowBulkImportProxiesAsync()
    {
        // Pull existing URLs once (off-thread) so the parser can flag
        // duplicates as the user types.
        var existingUrls = (await _proxies.ListAsync())
            .Select(p => p.Url).ToList();

        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new BulkImportProxiesDialog(existingUrls)
            {
                Owner = Application.Current.MainWindow,
            };
            var ok = dlg.ShowDialog() == true;
            _log.LogDebug("BulkImport closed: ok={Ok}, picked={Picked}",
                ok, dlg.Result?.Count ?? 0);
            return ok ? dlg.Result : null;
        });
    }

    public Task<bool> ConfirmAsync(
        string title, string message,
        string confirmLabel = "Confirm",
        ConfirmSeverity severity = ConfirmSeverity.Neutral)
    {
        var result = Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new ConfirmDialog(title, message, confirmLabel, severity)
            {
                Owner = Application.Current.MainWindow,
            };
            return dlg.ShowDialog() == true;
        });
        _log.LogInformation("Confirm '{Title}' [{Sev}] → {Result}", title, severity, result);
        return Task.FromResult(result);
    }
}

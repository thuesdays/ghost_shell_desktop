// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.App.Dialogs;

/// <summary>
/// Owns modal-window lifetime so view-models don't reference WPF
/// `Window` directly. Implementations live in this project; the
/// interface stays clean enough that we could swap to a different
/// shell technology without rewriting VMs.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Show the Create / Edit Profile modal. Pass `existing` to edit
    /// in place (the dialog pre-fills its fields). Returns the saved
    /// Profile, or null if the user cancelled.
    /// </summary>
    Task<Profile?> ShowProfileEditorAsync(Profile? existing = null);

    /// <summary>
    /// Bulk-create-profiles modal. Mirrors the legacy web's
    /// "+ Bulk" button: prefix + count + start index + language +
    /// FP template + proxy pool + enrich-on-first-run. The dialog
    /// performs the actual create itself (so progress can stream
    /// inline) and returns true if any profile was created — the
    /// caller refreshes its list.
    /// </summary>
    Task<bool> ShowBulkCreateProfilesAsync();

    /// <summary>
    /// Create / Edit Group modal. Pass <paramref name="existing"/>
    /// to edit in place; pass null to create. The dialog persists
    /// itself via <see cref="IProfileGroupService"/> and returns
    /// true if a change was saved.
    /// </summary>
    Task<bool> ShowGroupEditorAsync(
        ProfileGroup? existing,
        IReadOnlyList<Profile> allProfiles);

    /// <summary>
    /// Create / Edit Schedule modal. Pass <paramref name="existing"/>
    /// to edit in place; pass null to create. The dialog persists
    /// itself via <c>IScheduleService</c> and returns true if a
    /// change was saved.
    /// </summary>
    Task<bool> ShowScheduleEditorAsync(
        Schedule? existing,
        IReadOnlyList<string> profileNames,
        IReadOnlyList<string> groupNames);

    /// <summary>
    /// Same shape but for proxies. <paramref name="onRotateNow"/> is
    /// invoked when the user clicks "Rotate IP now" inside the dialog
    /// (only in Edit mode with a rotation URL filled). The callback
    /// returns a status string the dialog will surface back to the user
    /// — null/empty means "use the default success message".
    /// </summary>
    Task<Proxy?> ShowProxyEditorAsync(
        Proxy? existing = null,
        Func<string /* slug */, string /* rotateUrl */, Task<string?>>? onRotateNow = null);

    /// <summary>
    /// Bulk-import dialog with live-preview parser. Returns the list
    /// of parsed (and de-duplicated) proxies the user wants to commit,
    /// or null if they cancelled. The caller persists via
    /// IProxyService.BulkCreateAsync.
    /// </summary>
    Task<IReadOnlyList<ParsedProxy>?> ShowBulkImportProxiesAsync();

    /// <summary>
    /// Yes/No confirmation modal. Returns true on confirm. Used for
    /// delete and other destructive actions, plus error/info pop-ups
    /// (for which the caller can pass <c>"OK"</c> as confirmLabel).
    /// </summary>
    Task<bool> ConfirmAsync(
        string title, string message,
        string confirmLabel = "Confirm",
        ConfirmSeverity severity = ConfirmSeverity.Neutral);
}

/// <summary>
/// Visual flavour for <see cref="IDialogService.ConfirmAsync"/>:
/// drives the header icon, header tint, and confirm-button colour.
/// </summary>
public enum ConfirmSeverity
{
    Neutral,
    Info,
    Success,
    Warning,
    /// <summary>Red header + alert icon. Used for crash / launch-failed reports.</summary>
    Error,
    /// <summary>Red confirm button (still neutral header). Used for irreversible deletes.</summary>
    Danger,
}

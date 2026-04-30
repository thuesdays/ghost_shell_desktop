// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Warmup robot — visits a curated list of sites in a real Chromium
/// session so the profile accumulates an organic-looking cookie
/// trail before its first commercial run.
///
/// Matches the legacy Python <c>WarmupEngine</c> + <c>db.warmup_*</c>
/// surface; the Sessions page renders this service's history list and
/// triggers <see cref="StartAsync"/> from its "Run warmup now" button.
///
/// Concurrency rules:
///   • At most one warmup per profile at a time. <see cref="StartAsync"/>
///     throws <see cref="InvalidOperationException"/> if the profile is
///     already mid-warmup.
///   • The service does NOT itself prevent overlap with a regular
///     monitor run launched via <see cref="IProfileRunner"/>; the UI
///     should disable the Run button when the profile is active.
///     (Reason: warmup borrows the same Chromium binary; two windows
///     sharing one profile dir is undefined behaviour.)
///
/// The engine writes a <c>cookie_snapshots</c> row at the end of a
/// successful warmup with <c>trigger = 'auto_warmup'</c> — that row
/// is what subsequent profile launches will auto-restore from.
/// </summary>
public interface IWarmupService
{
    /// <summary>
    /// Static catalog of presets. Always non-empty; the UI binds to
    /// this list to render preset cards.
    /// </summary>
    IReadOnlyList<WarmupPresetDef> Presets { get; }

    /// <summary>
    /// Profiles with a currently-running warmup. Mirrors
    /// <see cref="IProfileRunner.ActiveProfileNames"/> for the UI to
    /// disable the Run-warmup button on a per-row basis.
    /// </summary>
    IReadOnlySet<string> ActiveProfileNames { get; }

    /// <summary>Fired when <see cref="ActiveProfileNames"/> changes.</summary>
    event EventHandler? ActiveChanged;

    /// <summary>
    /// Launch a warmup for <paramref name="profileName"/> using the named
    /// preset. <paramref name="siteCount"/> is the desired number of
    /// sites; preset.Sites.Count is the upper bound. Returns the new
    /// <c>warmup_runs.id</c> immediately — the actual browser work
    /// runs on a background task. Use <see cref="ListHistoryAsync"/>
    /// (or subscribe to <see cref="ActiveChanged"/>) to poll progress.
    /// </summary>
    /// <param name="trigger">
    /// Free-form: "manual", "scheduled", "auto_quality", etc.
    /// </param>
    Task<long> StartAsync(
        string profileName,
        string presetId,
        int siteCount,
        string trigger = "manual",
        CancellationToken ct = default);

    /// <summary>
    /// Cancel an in-flight warmup. No-op if the profile isn't running
    /// a warmup. Returns true if a warmup was actually cancelled.
    /// </summary>
    Task<bool> CancelAsync(string profileName, CancellationToken ct = default);

    /// <summary>
    /// Recent warmup-run rows for one profile, newest first.
    /// </summary>
    Task<IReadOnlyList<WarmupRun>> ListHistoryAsync(
        string? profileName = null, int limit = 50, CancellationToken ct = default);

    /// <summary>
    /// Latest warmup row for the profile (running or finished), or null
    /// if the profile has never been warmed up. Powers the LAST WARMUP
    /// + WARMUP STATUS stat boxes.
    /// </summary>
    Task<WarmupRun?> GetLatestAsync(string profileName, CancellationToken ct = default);
}

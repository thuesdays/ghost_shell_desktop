// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// CRUD + ETag-validated save for the Scripts library. Concurrency:
/// the editor passes the ETag it loaded with; <see cref="UpdateAsync"/>
/// rejects the write if the row has been touched in the meantime.
/// </summary>
public interface IScriptService
{
    Task<IReadOnlyList<Script>> ListAsync(CancellationToken ct = default);
    Task<Script?> GetAsync(long id, CancellationToken ct = default);
    Task<Script> CreateAsync(Script script, CancellationToken ct = default);

    /// <summary>Save changes. <paramref name="expectedEtag"/> must match
    /// the row's current ETag — pass the value you loaded with. Throws
    /// <see cref="System.InvalidOperationException"/> on conflict.</summary>
    Task UpdateAsync(Script script, string expectedEtag, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task<long> RecordRunAsync(ScriptRun run, CancellationToken ct = default);
    Task<IReadOnlyList<ScriptRun>> ListRunsAsync(
        long? scriptId = null, string? profileName = null,
        int limit = 50, CancellationToken ct = default);

    // ─── Phase 12 iter 6 ─────────────────────────────────────────

    /// <summary>
    /// Mark <paramref name="id"/> as the default script — clears the
    /// <c>is_default</c> flag on any other row first. Pass 0 to clear
    /// the default entirely (no auto-assignment).
    /// </summary>
    Task SetDefaultAsync(long id, CancellationToken ct = default);

    /// <summary>The current default script, or null.</summary>
    Task<Script?> GetDefaultAsync(CancellationToken ct = default);

    /// <summary>Assign <paramref name="scriptId"/> to <paramref name="profileNames"/>.
    /// Pass null/0 to clear the assignment.</summary>
    Task AssignToProfilesAsync(
        long? scriptId, IEnumerable<string> profileNames, CancellationToken ct = default);
}

/// <summary>
/// Executes a script's steps against a live browser session. Owns
/// the per-step timing + per-step exception handling. Doesn't manage
/// browser lifecycle — caller passes a started session.
/// </summary>
public interface IScriptRunner
{
    /// <summary>
    /// Run all steps in order. <paramref name="run"/> is updated
    /// in-flight via <see cref="IScriptService.RecordRunAsync"/>.
    /// Returns the terminal state.
    ///
    /// <paramref name="myDomains"/> + <paramref name="targetDomains"/>
    /// seed the per-step domain-filter gates (skip_on_my_domain,
    /// skip_on_target, only_on_target, only_on_my_domain) and the
    /// matching condition kinds (ad_is_mine, ad_is_target, etc.).
    /// Both default to empty (filters become no-ops).
    /// </summary>
    Task<ScriptRun> ExecuteAsync(
        Script script,
        IBrowserSession session,
        string profileName,
        CancellationToken ct = default,
        IEnumerable<string>? myDomains = null,
        IEnumerable<string>? targetDomains = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? vault = null,
        IReadOnlyDictionary<string, string>? vaultAliases = null);
}

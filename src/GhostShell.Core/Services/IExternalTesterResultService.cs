// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 31 — persistence for the external-fingerprint-tester probe
/// results so the cards on the Fingerprint page restore their last
/// verdict when the user revisits the page.
/// </summary>
public interface IExternalTesterResultService
{
    Task UpsertAsync(
        string profileName, string testerName,
        string summary, string verdict, string detailsJson,
        DateTime capturedUtc, CancellationToken ct = default);

    /// <summary>All persisted results for a profile, keyed by tester
    /// name. Empty dict if the profile hasn't been probed yet.</summary>
    Task<IReadOnlyDictionary<string, ExternalTesterRecord>> ListForProfileAsync(
        string profileName, CancellationToken ct = default);

    /// <summary>Wipe a profile's results (cascade hook on profile delete).</summary>
    Task ClearForProfileAsync(string profileName, CancellationToken ct = default);
}

public sealed record ExternalTesterRecord
{
    public string ProfileName { get; init; } = "";
    public string TesterName  { get; init; } = "";
    public string Summary     { get; init; } = "";
    public string Verdict     { get; init; } = "";
    public string DetailsJson { get; init; } = "[]";
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
}

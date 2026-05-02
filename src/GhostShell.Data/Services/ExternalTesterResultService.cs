// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using Dapper;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

internal sealed class ExternalTesterResultService : IExternalTesterResultService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<ExternalTesterResultService> _log;

    public ExternalTesterResultService(DatabaseConnection db, ILogger<ExternalTesterResultService> log)
    {
        _db = db;
        _log = log;
    }

    public Task UpsertAsync(
        string profileName, string testerName,
        string summary, string verdict, string detailsJson,
        DateTime capturedUtc, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO external_tester_results
              (profile_name, tester_name, summary, verdict, details_json, captured_at)
            VALUES
              (@profile, @tester, @summary, @verdict, @details, @captured)
            ON CONFLICT(profile_name, tester_name) DO UPDATE SET
              summary      = excluded.summary,
              verdict      = excluded.verdict,
              details_json = excluded.details_json,
              captured_at  = excluded.captured_at;
        """;
        return _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            profile  = profileName,
            tester   = testerName,
            summary,
            verdict,
            details  = detailsJson,
            captured = capturedUtc.ToString("O"),
        }), ct);
    }

    public async Task<IReadOnlyDictionary<string, ExternalTesterRecord>> ListForProfileAsync(
        string profileName, CancellationToken ct = default)
    {
        const string sql = """
            SELECT
              profile_name AS ProfileName,
              tester_name  AS TesterName,
              summary      AS Summary,
              verdict      AS Verdict,
              details_json AS DetailsJson,
              captured_at  AS CapturedAt
            FROM external_tester_results
            WHERE profile_name = @profile;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<ExternalTesterRecord>(
            sql, new { profile = profileName }), ct);
        var dict = new Dictionary<string, ExternalTesterRecord>(StringComparer.Ordinal);
        foreach (var r in rows) dict[r.TesterName] = r;
        return dict;
    }

    public Task ClearForProfileAsync(string profileName, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM external_tester_results WHERE profile_name = @profile;",
            new { profile = profileName }), ct);
}

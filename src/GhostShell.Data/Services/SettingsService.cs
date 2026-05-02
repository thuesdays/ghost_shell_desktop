// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Globalization;
using Dapper;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>Phase 29 — flat key/value SQLite-backed settings store.</summary>
internal sealed class SettingsService : ISettingsService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<SettingsService> _log;

    public SettingsService(DatabaseConnection db, ILogger<SettingsService> log)
    {
        _db = db;
        _log = log;
    }

    // ─── Generic ──────────────────────────────────────────────────────

    public Task<string?> GetStringAsync(string key, CancellationToken ct = default)
        => _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<string?>(
            "SELECT value FROM app_settings WHERE key = @key;", new { key }), ct);

    public async Task<int?> GetIntAsync(string key, CancellationToken ct = default)
    {
        var s = await GetStringAsync(key, ct);
        if (s is null) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    public async Task<double?> GetDoubleAsync(string key, CancellationToken ct = default)
    {
        var s = await GetStringAsync(key, ct);
        if (s is null) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    public async Task<bool?> GetBoolAsync(string key, CancellationToken ct = default)
    {
        var s = await GetStringAsync(key, ct);
        if (s is null) return null;
        return s switch
        {
            "1" or "true"  or "True"  or "yes" => true,
            "0" or "false" or "False" or "no"  => false,
            _ => null,
        };
    }

    public Task SetStringAsync(string key, string? value, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO app_settings (key, value, updated_at)
            VALUES (@key, @value, @now)
            ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @now;
        """;
        return _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            key,
            value,
            now = DateTime.UtcNow.ToString("O"),
        }), ct);
    }

    public Task SetIntAsync(string key, int value, CancellationToken ct = default)
        => SetStringAsync(key, value.ToString(CultureInfo.InvariantCulture), ct);

    public Task SetDoubleAsync(string key, double value, CancellationToken ct = default)
        => SetStringAsync(key, value.ToString("R", CultureInfo.InvariantCulture), ct);

    public Task SetBoolAsync(string key, bool value, CancellationToken ct = default)
        => SetStringAsync(key, value ? "true" : "false", ct);

    public Task DeleteAsync(string key, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM app_settings WHERE key = @key;", new { key }), ct);

    public async Task<IReadOnlyDictionary<string, string?>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _db.QueueAsync(c => c.QueryAsync<(string Key, string? Value)>(
            "SELECT key AS Key, value AS Value FROM app_settings ORDER BY key;"), ct);
        var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var r in rows) dict[r.Key] = r.Value;
        return dict;
    }

    public async Task ApplyAllAsync(
        IReadOnlyDictionary<string, string?> values, bool replaceAll = false,
        CancellationToken ct = default)
    {
        await _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();
            if (replaceAll)
                await c.ExecuteAsync("DELETE FROM app_settings;", transaction: tx);
            const string upsert = """
                INSERT INTO app_settings (key, value, updated_at)
                VALUES (@key, @value, @now)
                ON CONFLICT(key) DO UPDATE SET value = @value, updated_at = @now;
            """;
            var nowIso = DateTime.UtcNow.ToString("O");
            foreach (var kv in values)
            {
                await c.ExecuteAsync(upsert,
                    new { key = kv.Key, value = kv.Value, now = nowIso }, tx);
            }
            tx.Commit();
            return 0;
        }, ct);
        _log.LogInformation(
            "Settings applied: {Count} key(s), mode={Mode}",
            values.Count, replaceAll ? "replace" : "merge");
    }

    // ─── Strongly-typed shortcuts ─────────────────────────────────────

    public async Task<int> GetUaSpoofMinAsync(CancellationToken ct = default)
        => await GetIntAsync(SettingsKeys.UaSpoofMin, ct) ?? 130;

    public async Task<int> GetUaSpoofMaxAsync(CancellationToken ct = default)
        => await GetIntAsync(SettingsKeys.UaSpoofMax, ct) ?? 147;

    public async Task SetUaSpoofRangeAsync(int min, int max, CancellationToken ct = default)
    {
        // Defensive — clamp to a reasonable Chrome major-version window.
        min = Math.Clamp(min, 100, 200);
        max = Math.Clamp(max, 100, 200);
        if (min > max) (min, max) = (max, min);
        await SetIntAsync(SettingsKeys.UaSpoofMin, min, ct);
        await SetIntAsync(SettingsKeys.UaSpoofMax, max, ct);
    }

    public Task<string?> GetChromiumBinaryPathAsync(CancellationToken ct = default)
        => GetStringAsync(SettingsKeys.ChromiumBinaryPath, ct);

    public Task SetChromiumBinaryPathAsync(string? path, CancellationToken ct = default)
        => SetStringAsync(SettingsKeys.ChromiumBinaryPath, path, ct);

    public async Task<bool> GetSerpScrollEnabledAsync(CancellationToken ct = default)
        => await GetBoolAsync(SettingsKeys.SerpScrollEnabled, ct) ?? true;

    public async Task<bool> GetSerpDwellEnabledAsync(CancellationToken ct = default)
        => await GetBoolAsync(SettingsKeys.SerpDwellEnabled, ct) ?? true;

    public async Task<bool> GetOrganicClickEnabledAsync(CancellationToken ct = default)
        => await GetBoolAsync(SettingsKeys.OrganicClickEnabled, ct) ?? true;

    public async Task<double> GetOrganicClickProbabilityAsync(CancellationToken ct = default)
        => await GetDoubleAsync(SettingsKeys.OrganicClickProbability, ct) ?? 0.25;

    public async Task<int> GetOrganicDwellMinSecAsync(CancellationToken ct = default)
        => await GetIntAsync(SettingsKeys.OrganicDwellMinSec, ct) ?? 8;

    public async Task<int> GetOrganicDwellMaxSecAsync(CancellationToken ct = default)
        => await GetIntAsync(SettingsKeys.OrganicDwellMaxSec, ct) ?? 26;

    public async Task<bool> GetAutoEnrichEnabledAsync(CancellationToken ct = default)
        => await GetBoolAsync(SettingsKeys.AutoEnrichEnabled, ct) ?? false;

    public async Task<int> GetAutoEnrichMaxDaysAsync(CancellationToken ct = default)
        => await GetIntAsync(SettingsKeys.AutoEnrichMaxDays, ct) ?? 30;

    public async Task<int> GetAutoEnrichMaxUrlsAsync(CancellationToken ct = default)
        => await GetIntAsync(SettingsKeys.AutoEnrichMaxUrls, ct) ?? 500;

    public Task<string?> GetAutoEnrichSourcePathAsync(CancellationToken ct = default)
        => GetStringAsync(SettingsKeys.AutoEnrichSourcePath, ct);
}

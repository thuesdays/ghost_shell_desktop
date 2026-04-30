// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Dapper;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

public sealed class CookiePackService : ICookiePackService
{
    private readonly DatabaseConnection _db;
    private readonly ILogger<CookiePackService> _log;

    public CookiePackService(DatabaseConnection db, ILogger<CookiePackService> log)
    {
        _db  = db;
        _log = log;
    }

    private const string MetadataColumns = """
        id,
        slug,
        label,
        domains,
        age_days       AS AgeDays,
        captcha_rate   AS CaptchaRate,
        cookies_count  AS CookiesCount,
        storage_count  AS StorageCount,
        created_at     AS CreatedAt,
        updated_at     AS UpdatedAt
    """;

    // ─── Read paths ──────────────────────────────────────────────

    public async Task<IReadOnlyList<CookiePack>> ListAsync(CancellationToken ct = default)
    {
        var rows = await _db.QueueAsync(c => c.QueryAsync<DbRow>(
            $"SELECT {MetadataColumns} FROM cookie_packs ORDER BY updated_at DESC;"), ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<CookiePack?> GetAsync(long id, CancellationToken ct = default)
    {
        var row = await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<DbRow>(
            $"SELECT {MetadataColumns} FROM cookie_packs WHERE id = @id;",
            new { id }), ct);
        return row is null ? null : ToModel(row);
    }

    public async Task<SessionPayload?> GetPayloadAsync(long id, CancellationToken ct = default)
    {
        var blob = await _db.QueueAsync(c => c.ExecuteScalarAsync<byte[]?>(
            "SELECT payload_gz FROM cookie_packs WHERE id = @id;", new { id }), ct);
        if (blob is null || blob.Length == 0) return null;
        return DecodePayload(blob);
    }

    // ─── Mutations ───────────────────────────────────────────────

    public async Task<long> UpsertAsync(
        CookiePack meta, SessionPayload payload, CancellationToken ct = default)
    {
        var domainsJson = JsonSerializer.Serialize(meta.Domains, SessionPayloadJson.Options);
        var payloadGz   = EncodePayload(payload);
        var now         = DateTime.UtcNow.ToString("O");

        // ON CONFLICT(slug) does the UPSERT — re-importing the same
        // pack overwrites without dup-key error. RETURNING id picks
        // up the existing id on update.
        const string sql = """
            INSERT INTO cookie_packs
                (slug, label, domains, age_days, captcha_rate,
                 payload_gz, cookies_count, storage_count,
                 created_at, updated_at)
            VALUES
                (@Slug, @Label, @Domains, @AgeDays, @CaptchaRate,
                 @PayloadGz, @CookiesCount, @StorageCount,
                 @CreatedAt, @UpdatedAt)
            ON CONFLICT(slug) DO UPDATE SET
                label         = excluded.label,
                domains       = excluded.domains,
                age_days      = excluded.age_days,
                captcha_rate  = excluded.captcha_rate,
                payload_gz    = excluded.payload_gz,
                cookies_count = excluded.cookies_count,
                storage_count = excluded.storage_count,
                updated_at    = excluded.updated_at
            RETURNING id;
        """;

        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            meta.Slug,
            meta.Label,
            Domains       = domainsJson,
            meta.AgeDays,
            meta.CaptchaRate,
            PayloadGz     = payloadGz,
            CookiesCount  = payload.Cookies.Count,
            StorageCount  = payload.Storage.Count,
            CreatedAt     = now,
            UpdatedAt     = now,
        }), ct);

        _log.LogInformation(
            "Pack '{Slug}' upserted (id={Id}, cookies={Cookies}, storage={Storage})",
            meta.Slug, id, payload.Cookies.Count, payload.Storage.Count);
        return id;
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        var rows = await _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM cookie_packs WHERE id = @id;", new { id }), ct);
        if (rows > 0)
            _log.LogInformation("Pack #{Id} deleted", id);
    }

    // ─── Live runtime hooks ──────────────────────────────────────

    public async Task<ApplyResult> ApplyAsync(
        long packId, IBrowserSession session, CancellationToken ct = default)
    {
        var payload = await GetPayloadAsync(packId, ct)
            ?? throw new InvalidOperationException(
                $"Pack #{packId} has no payload (deleted or corrupted).");

        // Cookies first — they don't need navigation, the CDP path
        // pushes them straight into the cookie store.
        await session.SetCookiesAsync(payload.Cookies, ct);

        // Storage requires a per-origin navigation. The session
        // implementation handles "no localStorage on this origin →
        // skip" internally so we don't burn navigation time on
        // empty entries.
        await session.SetStorageAsync(payload.Storage, ct);

        _log.LogInformation(
            "Applied pack #{Id} to '{Profile}' ({Cookies} cookies, {Origins} storage origins)",
            packId, session.ProfileName, payload.Cookies.Count, payload.Storage.Count);
        return new ApplyResult(payload.Cookies.Count, payload.Storage.Count);
    }

    public async Task<long> ExportFromSessionAsync(
        string slug, string label, IBrowserSession session,
        CancellationToken ct = default)
    {
        var cookies = await session.GetCookiesAsync(ct);

        // Distinct origins from cookies — derive https://<domain>.
        // We deliberately don't probe more origins than the cookies
        // already attest to; visiting random sites just to drain
        // their localStorage isn't a thing the user asked for.
        var origins = cookies
            .Select(c => "https://" + c.Domain.TrimStart('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var storage = await session.GetStorageAsync(origins, ct);

        var domains = cookies
            .Select(c => c.Domain.TrimStart('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var meta = new CookiePack
        {
            Slug         = slug,
            Label        = label,
            Domains      = domains,
            AgeDays      = 0,
            CaptchaRate  = 0,
            CookiesCount = cookies.Count,
            StorageCount = storage.Count,
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        };
        var payload = new SessionPayload { Cookies = cookies, Storage = storage };
        return await UpsertAsync(meta, payload, ct);
    }

    // ─── Encoding helpers ────────────────────────────────────────

    private static byte[] EncodePayload(SessionPayload payload)
    {
        var json = SessionPayloadJson.SerializePayload(payload);
        var raw  = Encoding.UTF8.GetBytes(json);
        using var ms  = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static SessionPayload DecodePayload(byte[] gz)
    {
        using var input  = new MemoryStream(gz);
        using var dec    = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(dec, Encoding.UTF8);
        var json = reader.ReadToEnd();
        return SessionPayloadJson.DeserializePayload(json) ?? SessionPayload.Empty;
    }

    private static CookiePack ToModel(DbRow r) => new()
    {
        Id            = r.Id,
        Slug          = r.Slug,
        Label         = r.Label,
        Domains       = string.IsNullOrWhiteSpace(r.Domains)
                          ? Array.Empty<string>()
                          : (JsonSerializer.Deserialize<List<string>>(
                                r.Domains, SessionPayloadJson.Options)
                              ?? new List<string>()),
        AgeDays       = r.AgeDays,
        CaptchaRate   = r.CaptchaRate,
        CookiesCount  = r.CookiesCount,
        StorageCount  = r.StorageCount,
        CreatedAt     = DateTime.TryParse(r.CreatedAt, out var c) ? c : default,
        UpdatedAt     = DateTime.TryParse(r.UpdatedAt, out var u) ? u : default,
    };

    /// <summary>
    /// Init-only record (NOT positional). SQLite returns INTEGER as
    /// Int64, but our column types are <c>int</c>. A positional ctor
    /// of <c>(int, ...)</c> forces Dapper to look for a literal
    /// <c>(long, ...)</c> constructor signature and throws at
    /// materialization. With init-properties Dapper falls back to
    /// name-based set-by-set assignment with the standard type
    /// converter (int64 → int32). Same fix already applied to
    /// <c>RunStats</c>.
    /// </summary>
    private sealed record DbRow
    {
        public long   Id            { get; init; }
        public string Slug          { get; init; } = "";
        public string Label         { get; init; } = "";
        public string Domains       { get; init; } = "";
        public int    AgeDays       { get; init; }
        public double CaptchaRate   { get; init; }
        public int    CookiesCount  { get; init; }
        public int    StorageCount  { get; init; }
        public string CreatedAt     { get; init; } = "";
        public string UpdatedAt     { get; init; } = "";
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using Dapper;
using GhostShell.Core.Common;
using GhostShell.Core.Extensions;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Data.Database;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// Phase 27 — extension library implementation.
///
/// Stores everything under <c>%LocalAppData%\GhostShell\extensions\&lt;ext_id&gt;\</c>.
/// Three install paths:
///   • <see cref="InstallFromZipAsync"/>   — .zip / .crx archive
///   • <see cref="InstallFromFolderAsync"/> — already-unpacked folder
///   • <see cref="InstallFromStoreAsync"/>  — Chrome Web Store ID
/// Each path ends in the same place: a row in <c>extensions</c> + an
/// unpacked dir on disk. The browser launcher reads the rows back via
/// <see cref="ListEnabledForProfileAsync"/> when it builds the
/// <c>--load-extension=</c> flag.
/// </summary>
internal sealed class ExtensionService : IExtensionService
{
    /// <summary>Public-update endpoint that serves the latest .crx for
    /// a given extension ID. Documented at
    /// https://chromium.googlesource.com/chromium/src/+/refs/heads/main/docs/extension_blocklist.md
    /// — still works without auth and is what corporate "force-installed
    /// extension" deployments use under the hood.</summary>
    private const string CrxDownloadUrlTemplate =
        "https://clients2.google.com/service/update2/crx" +
        "?response=redirect&prodversion=125.0&acceptformat=crx2,crx3&x=id%3D{0}%26uc";

    private readonly DatabaseConnection _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ExtensionService> _log;

    /// <summary>Phase 27 audit fix — per-ExtId install gate. Two
    /// concurrent installs of the same extension would race on the
    /// final unpacked dir (Directory.Delete + Create + CopyDirectory),
    /// producing file-in-use errors and half-copied state. We hash
    /// ext_id to a SemaphoreSlim and serialise inside FinalizeStaged.
    /// Process-local; multi-instance is out of scope (each app instance
    /// has its own DataDir already).</summary>
    private static readonly Dictionary<string, SemaphoreSlim> _installGates = new();
    private static readonly object _installGatesLock = new();
    private static SemaphoreSlim GetInstallGate(string extId)
    {
        lock (_installGatesLock)
        {
            if (!_installGates.TryGetValue(extId, out var gate))
                _installGates[extId] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    public ExtensionService(
        DatabaseConnection db,
        IHttpClientFactory httpFactory,
        ILogger<ExtensionService> log)
    {
        _db          = db;
        _httpFactory = httpFactory;
        _log         = log;
    }

    /// <summary>Build a fresh HttpClient with Chrome's own User-Agent.
    /// The CWS update endpoint serves stale builds to unfamiliar UAs,
    /// so we always identify as a recent Chrome on Windows.</summary>
    private HttpClient NewHttp()
    {
        var http = _httpFactory.CreateClient(nameof(ExtensionService));
        // TryAddWithoutValidation: the typed UserAgent.ParseAdd path
        // strict-parses the structured header (RFC 7231 product-token),
        // which rejects valid Chrome UAs that include parenthesised
        // comments without surrounding spaces. WithoutValidation just
        // appends the raw header verbatim — what we want here.
        if (!http.DefaultRequestHeaders.UserAgent.Any())
        {
            http.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        }
        http.Timeout = TimeSpan.FromSeconds(60);
        return http;
    }

    // ─── Install paths ────────────────────────────────────────────────

    public async Task<ExtensionItem> InstallFromZipAsync(string archivePath, CancellationToken ct = default)
    {
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("archive not found", archivePath);

        // Stage to a temp dir first; we'll move into the final
        // ext_id-based location once we've parsed the manifest and
        // know the target ID.
        var stage = Path.Combine(Path.GetTempPath(),
            "ghostshell_ext_stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            ExtractArchive(archivePath, stage, ct);
            var sourceLabel = string.Equals(
                Path.GetExtension(archivePath), ".crx", StringComparison.OrdinalIgnoreCase)
                ? "crx" : "zip";
            return await FinalizeStagedInstallAsync(
                stage, sourceLabel, archivePath, ct);
        }
        finally
        {
            TryDelete(stage);
        }
    }

    public async Task<ExtensionItem> InstallFromFolderAsync(string folderPath, CancellationToken ct = default)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException("folder not found");
        // Copy into a stage so the user can move/delete the source
        // without breaking the install.
        var stage = Path.Combine(Path.GetTempPath(),
            "ghostshell_ext_stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            CopyDirectory(folderPath, stage, ct);
            return await FinalizeStagedInstallAsync(stage, "folder", folderPath, ct);
        }
        finally
        {
            TryDelete(stage);
        }
    }

    public async Task<ExtensionItem> InstallFromStoreAsync(string cwsExtId, CancellationToken ct = default)
    {
        // Phase 27 audit fix — validate the ID FORMAT, not just length:
        // CWS IDs are exactly 32 chars in [a-p]. Without the char-class
        // check, an attacker (or a user typo) could inject anything that
        // happens to be 32 chars wide into the URL template. The HTTP
        // client URL-encodes for us, but garbage IDs still produce 404s
        // and waste a round-trip; reject here.
        if (string.IsNullOrWhiteSpace(cwsExtId) || cwsExtId.Length != 32 ||
            !cwsExtId.All(c => c is >= 'a' and <= 'p'))
            throw new ArgumentException(
                "Chrome Web Store IDs are 32 characters in [a-p]", nameof(cwsExtId));

        var url = string.Format(CrxDownloadUrlTemplate, cwsExtId);
        _log.LogInformation("Downloading extension {Id} from CWS", cwsExtId);

        // 1. Fetch the .crx blob.
        using var http = NewHttp();
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var tempCrx = Path.Combine(Path.GetTempPath(), $"ghostshell_cws_{cwsExtId}.crx");
        try
        {
            await using (var fs = File.Create(tempCrx))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }
            // 2. Unpack via the same path as a local .crx install.
            var item = await InstallFromZipAsync(tempCrx, ct);
            // 3. Override source + install URL so the UI shows
            //    "from store" + the CWS link.
            await UpdateSourceMetadataAsync(item.Id, "store",
                $"https://chromewebstore.google.com/detail/{cwsExtId}", ct);
            return item with { Source = "store",
                InstallUrl = $"https://chromewebstore.google.com/detail/{cwsExtId}" };
        }
        finally
        {
            TryDelete(tempCrx);
        }
    }

    private async Task<ExtensionItem> FinalizeStagedInstallAsync(
        string stagedDir, string source, string installUrl, CancellationToken ct)
    {
        // Locate manifest.json — sometimes the archive contains a single
        // top-level subdirectory; we tolerate one level of nesting.
        var manifestPath = Path.Combine(stagedDir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            var nested = Directory.GetDirectories(stagedDir)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "manifest.json")));
            if (nested is not null)
            {
                manifestPath = Path.Combine(nested, "manifest.json");
                stagedDir = nested;
            }
            else
            {
                throw new InvalidDataException(
                    "No manifest.json found in the archive — not a valid Chrome extension.");
            }
        }

        var manifest = ExtensionManifest.ParseFromFile(manifestPath, stagedDir);

        // Phase 27 audit fix — the synthesized id is computed from the
        // STAGE path, which is random. We anchor it to the FINAL path
        // by re-parsing AFTER the copy. The first parse just gives us
        // a placeholder id used to allocate the final dir. On re-install
        // of the same source, the final id is deterministic and
        // GetInstallGate(id) serialises the dir-rewrite.
        var finalDir = AppPaths.ExtensionDir(manifest.ExtId);

        // Phase 27 audit fix — per-id install gate. Two concurrent
        // installs of the same extension would race on the
        // Directory.Delete + CreateDirectory + CopyDirectory sequence
        // below, producing file-in-use errors and half-copied state.
        var gate = GetInstallGate(manifest.ExtId);
        await gate.WaitAsync(ct);
        try
        {
            // Wipe target dir if a previous install left junk; we'll
            // overwrite cleanly.
            if (Directory.Exists(finalDir)) Directory.Delete(finalDir, recursive: true);
            Directory.CreateDirectory(finalDir);
            CopyDirectory(stagedDir, finalDir, ct);

            // Re-read the manifest from the final path so the manifest's
            // ExtId derivation is anchored to the deterministic path
            // (ParseFromFile uses unpackedDir for hashing on key-less
            // manifests).
            var finalManifestPath = Path.Combine(finalDir, "manifest.json");
            manifest = ExtensionManifest.ParseFromFile(finalManifestPath, finalDir);
        }
        finally
        {
            gate.Release();
        }

        var newRow = new ExtensionItem
        {
            ExtId        = manifest.ExtId,
            Name         = manifest.Name,
            Version      = manifest.Version,
            Description  = manifest.Description,
            Author       = manifest.Author,
            Homepage     = manifest.Homepage,
            Source       = source,
            InstallUrl   = installUrl,
            LocalPath    = finalDir,
            ManifestJson = manifest.RawJson,
            IconPath     = manifest.IconPath is null
                ? null : Path.Combine(finalDir, manifest.IconPath),
            PermissionsJson     = JsonSerializer.Serialize(manifest.Permissions),
            HostPermissionsJson = JsonSerializer.Serialize(manifest.HostPermissions),
            Enabled      = true,
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        };
        return await UpsertExtensionAsync(newRow, ct);
    }

    private async Task<ExtensionItem> UpsertExtensionAsync(ExtensionItem item, CancellationToken ct)
    {
        // Phase 27 audit-friendly UPSERT — on duplicate ext_id we
        // refresh metadata + bump updated_at; otherwise insert. Returns
        // the row with its assigned id.
        const string sql = """
            INSERT INTO extensions
              (ext_id, name, version, description, author, homepage,
               source, install_url, local_path, manifest_json, icon_path,
               permissions_json, host_permissions_json, enabled, pinned,
               created_at, updated_at)
            VALUES
              (@ExtId, @Name, @Version, @Description, @Author, @Homepage,
               @Source, @InstallUrl, @LocalPath, @ManifestJson, @IconPath,
               @PermissionsJson, @HostPermissionsJson, @Enabled, @Pinned,
               @CreatedAt, @UpdatedAt)
            ON CONFLICT(ext_id) DO UPDATE SET
              name = excluded.name,
              version = excluded.version,
              description = excluded.description,
              author = excluded.author,
              homepage = excluded.homepage,
              source = excluded.source,
              install_url = excluded.install_url,
              local_path = excluded.local_path,
              manifest_json = excluded.manifest_json,
              icon_path = excluded.icon_path,
              permissions_json = excluded.permissions_json,
              host_permissions_json = excluded.host_permissions_json,
              updated_at = excluded.updated_at
            RETURNING id;
        """;
        var nowIso = DateTime.UtcNow.ToString("O");
        var id = await _db.QueueAsync(c => c.ExecuteScalarAsync<long>(sql, new
        {
            item.ExtId, item.Name, item.Version, item.Description, item.Author, item.Homepage,
            item.Source, item.InstallUrl, item.LocalPath, item.ManifestJson, item.IconPath,
            item.PermissionsJson, item.HostPermissionsJson,
            Enabled = item.Enabled ? 1 : 0,
            Pinned  = item.Pinned ? 1 : 0,
            CreatedAt = nowIso, UpdatedAt = nowIso,
        }), ct);
        _log.LogInformation("Extension '{Name}' v{Ver} installed (id={Id}, source={Src})",
            item.Name, item.Version, id, item.Source);
        return item with { Id = id };
    }

    private Task UpdateSourceMetadataAsync(long id, string source, string installUrl, CancellationToken ct)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE extensions SET source = @s, install_url = @u, updated_at = @t WHERE id = @id;",
            new { s = source, u = installUrl, t = DateTime.UtcNow.ToString("O"), id }), ct);

    // ─── CRUD ─────────────────────────────────────────────────────────

    private const string SelectColumns = """
        id, ext_id        AS ExtId,
        name, version, description, author, homepage,
        source, install_url AS InstallUrl,
        local_path        AS LocalPath,
        manifest_json     AS ManifestJson,
        icon_path         AS IconPath,
        permissions_json  AS PermissionsJson,
        host_permissions_json AS HostPermissionsJson,
        enabled, pinned,
        created_at        AS CreatedAt,
        updated_at        AS UpdatedAt
    """;

    public async Task<IReadOnlyList<ExtensionItem>> ListAsync(
        bool? enabledOnly = null, string? search = null, CancellationToken ct = default)
    {
        var where = new List<string>();
        var args = new DynamicParameters();
        if (enabledOnly is true)  { where.Add("enabled = 1"); }
        if (enabledOnly is false) { where.Add("enabled = 0"); }
        if (!string.IsNullOrWhiteSpace(search))
        {
            where.Add("(name LIKE @q OR description LIKE @q OR author LIKE @q)");
            args.Add("q", $"%{search}%");
        }
        var sql = $"SELECT {SelectColumns} FROM extensions"
                  + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
                  + " ORDER BY name COLLATE NOCASE LIMIT 1000;";
        var rows = await _db.QueueAsync(c => c.QueryAsync<ExtensionItem>(sql, args), ct);
        return rows.ToList();
    }

    public async Task<ExtensionItem?> GetAsync(long id, CancellationToken ct = default)
        => await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<ExtensionItem>(
            $"SELECT {SelectColumns} FROM extensions WHERE id = @id;", new { id }), ct);

    public async Task<ExtensionItem?> GetByExtIdAsync(string extId, CancellationToken ct = default)
        => await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<ExtensionItem>(
            $"SELECT {SelectColumns} FROM extensions WHERE ext_id = @extId;", new { extId }), ct);

    public Task SetGlobalEnabledAsync(long id, bool enabled, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE extensions SET enabled = @e, updated_at = @t WHERE id = @id;",
            new { e = enabled ? 1 : 0, t = DateTime.UtcNow.ToString("O"), id }), ct);

    public Task SetPinnedAsync(long id, bool pinned, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "UPDATE extensions SET pinned = @p, updated_at = @t WHERE id = @id;",
            new { p = pinned ? 1 : 0, t = DateTime.UtcNow.ToString("O"), id }), ct);

    public async Task UninstallAsync(long id, CancellationToken ct = default)
    {
        var item = await GetAsync(id, ct);
        if (item is null) return;
        await _db.QueueAsync(async c =>
        {
            using var tx = c.BeginTransaction();
            await c.ExecuteAsync("DELETE FROM extensions          WHERE id = @id;", new { id }, tx);
            await c.ExecuteAsync("DELETE FROM profile_extensions  WHERE extension_id = @id;", new { id }, tx);
            tx.Commit();
            return 0;
        }, ct);
        // Remove the unpacked dir last — DB row is the canonical
        // record so a partial failure leaves us with an orphan dir
        // (recoverable via "remove orphans" later) instead of a
        // ghost row.
        try
        {
            if (Directory.Exists(item.LocalPath))
                Directory.Delete(item.LocalPath, recursive: true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't remove extension dir {Dir}", item.LocalPath);
        }
        _log.LogInformation("Extension #{Id} uninstalled", id);
    }

    // ─── Per-profile overrides ────────────────────────────────────────

    public async Task<IReadOnlyList<ExtensionItem>> ListEnabledForProfileAsync(
        string profileName, CancellationToken ct = default)
    {
        // Effective enabled state:
        //   1. Per-profile row's enabled flag if present
        //   2. Otherwise the global enabled flag
        //
        // Phase 28 hot-fix — SelectColumns has bare column names (id,
        // name, enabled, …) which collide with the same-named columns
        // in profile_extensions during the JOIN ("ambiguous column name:
        // id"). Qualify every column with the `e.` alias for this query.
        const string sql = """
            SELECT
              e.id, e.ext_id AS ExtId,
              e.name, e.version, e.description, e.author, e.homepage,
              e.source, e.install_url AS InstallUrl,
              e.local_path AS LocalPath,
              e.manifest_json AS ManifestJson,
              e.icon_path AS IconPath,
              e.permissions_json AS PermissionsJson,
              e.host_permissions_json AS HostPermissionsJson,
              e.enabled, e.pinned,
              e.created_at AS CreatedAt,
              e.updated_at AS UpdatedAt
            FROM extensions e
            LEFT JOIN profile_extensions pe
              ON pe.extension_id = e.id AND pe.profile_name = @profile
            WHERE COALESCE(pe.enabled, e.enabled) = 1
            ORDER BY e.name COLLATE NOCASE;
        """;
        var rows = await _db.QueueAsync(c => c.QueryAsync<ExtensionItem>(
            sql, new { profile = profileName }), ct);
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<long, bool?>> GetProfileOverridesAsync(
        string profileName, CancellationToken ct = default)
    {
        const string sql =
            "SELECT extension_id AS Id, enabled AS Enabled " +
            "FROM profile_extensions WHERE profile_name = @profile;";
        var rows = await _db.QueueAsync(c => c.QueryAsync<(long Id, int Enabled)>(
            sql, new { profile = profileName }), ct);
        var dict = new Dictionary<long, bool?>();
        foreach (var r in rows) dict[r.Id] = r.Enabled == 1;
        return dict;
    }

    public Task SetEnabledForProfileAsync(
        string profileName, long extensionId, bool enabled, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO profile_extensions (profile_name, extension_id, enabled, updated_at)
            VALUES (@profile, @id, @e, @t)
            ON CONFLICT(profile_name, extension_id)
            DO UPDATE SET enabled = excluded.enabled, updated_at = excluded.updated_at;
        """;
        return _db.QueueAsync(c => c.ExecuteAsync(sql, new
        {
            profile = profileName,
            id = extensionId,
            e = enabled ? 1 : 0,
            t = DateTime.UtcNow.ToString("O"),
        }), ct);
    }

    public Task ClearProfileOverrideAsync(string profileName, long extensionId, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM profile_extensions WHERE profile_name = @profile AND extension_id = @id;",
            new { profile = profileName, id = extensionId }), ct);

    public Task ClearAllOverridesForProfileAsync(string profileName, CancellationToken ct = default)
        => _db.QueueAsync(c => c.ExecuteAsync(
            "DELETE FROM profile_extensions WHERE profile_name = @profile;",
            new { profile = profileName }), ct);

    // ─── Store catalog + lookup ───────────────────────────────────────

    public Task<IReadOnlyList<ExtensionStoreEntry>> GetCuratedCatalogAsync(
        string? search = null, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ExtensionStoreEntry>>(
            CuratedExtensionsCatalog.Search(search).ToList());

    public async Task<ExtensionStoreEntry?> LookupStoreAsync(
        string cwsExtId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cwsExtId) || cwsExtId.Length != 32) return null;

        // 1. Curated list first.
        var curated = CuratedExtensionsCatalog.Entries
            .FirstOrDefault(e => string.Equals(e.ExtId, cwsExtId, StringComparison.OrdinalIgnoreCase));
        if (curated is not null) return curated;

        // 2. Per-user cache.
        var cached = await _db.QueueAsync(c => c.QuerySingleOrDefaultAsync<ExtensionStoreEntry>(
            "SELECT ext_id AS ExtId, name, description, author, icon_url AS IconUrl, " +
            "       rating, users, last_seen_at AS LastSeenAt " +
            "FROM extension_store_cache WHERE ext_id = @id;",
            new { id = cwsExtId }), ct);
        if (cached is not null) return cached;

        // 3. Stub — we don't scrape the CWS. Return just the ID so the
        //    UI can surface "(unknown extension)" with an install button.
        return new ExtensionStoreEntry { ExtId = cwsExtId, Name = cwsExtId };
    }

    // ─── File-system helpers ──────────────────────────────────────────

    /// <summary>Extract a .zip or .crx into <paramref name="targetDir"/>.
    /// CRX-3 has a header before the zip payload — we strip it.</summary>
    private static void ExtractArchive(string archivePath, string targetDir, CancellationToken ct)
    {
        // CRX-3 magic: 'C', 'r', '2', '4' (Cr24) followed by uint32 version,
        // uint32 header_size, then the header bytes, then a standard ZIP.
        long zipStart = 0;
        long zipLength;
        byte[] zipBytes;
        using (var fs = File.OpenRead(archivePath))
        {
            Span<byte> magic = stackalloc byte[8]; // 4 magic + 4 version
            var read = fs.Read(magic);
            if (read >= 4 &&
                magic[0] == (byte)'C' && magic[1] == (byte)'r' &&
                magic[2] == (byte)'2' && magic[3] == (byte)'4')
            {
                // Read header_size (uint32 LE) at offset 8.
                Span<byte> hdrSize = stackalloc byte[4];
                fs.Position = 8;
                fs.Read(hdrSize);
                var headerBytes = (uint)(hdrSize[0] | (hdrSize[1] << 8) | (hdrSize[2] << 16) | (hdrSize[3] << 24));
                // Clamp header offset against file size (audit fix).
                zipStart = 12L + headerBytes;
                if (zipStart < 0 || zipStart >= fs.Length)
                    throw new InvalidDataException(
                        $"CRX header size ({headerBytes}) is larger than the file " +
                        $"({fs.Length} bytes). Refusing to install — the archive is " +
                        "corrupt or hostile.");
            }
            // Phase 27 fix — copy the ZIP portion into its own buffer.
            // ZipArchive computes offsets RELATIVE to the start of the
            // stream you hand it; if we hand it the full CRX file (with
            // a header in front), the End-of-Central-Directory record
            // points at offsets that don't match where the zip actually
            // starts in the bytes ZipArchive sees. The error you'd hit
            // is the classic "Number of entries expected in End Of
            // Central Directory does not correspond..." — that's exactly
            // the offset-mismatch failure mode. Fix: read the zip
            // payload only, into a MemoryStream that starts at byte 0.
            zipLength = fs.Length - zipStart;
            zipBytes = new byte[zipLength];
            fs.Position = zipStart;
            int total = 0;
            while (total < zipBytes.Length)
            {
                var n = fs.Read(zipBytes, total, zipBytes.Length - total);
                if (n <= 0) break;
                total += n;
            }
            if (total != zipBytes.Length)
                throw new InvalidDataException(
                    $"Short read while extracting CRX payload (expected {zipBytes.Length} got {total}).");
        }
        // Sanity check — the payload must start with the local-file-
        // header magic "PK\x03\x04". Anything else (e.g. CWS returned
        // an HTML error page) means we shouldn't even try to unzip; the
        // ZipArchive error message in that case is "End of Central
        // Directory does not correspond" which is unhelpful for the user.
        if (zipBytes.Length < 4 ||
            zipBytes[0] != 'P' || zipBytes[1] != 'K' ||
            zipBytes[2] != 0x03 || zipBytes[3] != 0x04)
        {
            throw new InvalidDataException(
                "Downloaded file is not a valid extension archive — the " +
                "Chrome Web Store may have returned an error page. Try " +
                "again, or use \"Custom URL\" with the extension's direct " +
                ".crx download link.");
        }
        using var ms = new MemoryStream(zipBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            // Skip directories (entries ending in /)
            if (string.IsNullOrEmpty(entry.Name)) continue;
            // Phase 27 — defence against zip-slip path traversal.
            var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            var rooted = Path.GetFullPath(targetDir);
            if (!dest.StartsWith(rooted + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                !dest.Equals(rooted, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Archive entry '{entry.FullName}' escapes target dir (zip-slip guard).");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }

    /// <summary>Cap on total bytes copied for a single extension —
    /// hard refusal if exceeded. 256 MB is well above any real
    /// extension while small enough that a stray "let's copy
    /// C:\\Windows" doesn't fill the disk before we notice.</summary>
    private const long MaxExtensionSizeBytes = 256L * 1024L * 1024L;

    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(target, rel));
        }
        // Phase 27 audit fix — refuse to copy more than 256 MB so a
        // user passing C:\Windows can't fill the disk. We accumulate
        // BEFORE copying so we never start writing past the limit.
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            total += info.Length;
            if (total > MaxExtensionSizeBytes)
                throw new InvalidDataException(
                    $"Extension exceeds size cap ({MaxExtensionSizeBytes / (1024 * 1024)} MB). " +
                    "Repackage the extension or split out unused assets before installing.");
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void TryDelete(string pathOrDir)
    {
        try
        {
            if (File.Exists(pathOrDir)) File.Delete(pathOrDir);
            else if (Directory.Exists(pathOrDir)) Directory.Delete(pathOrDir, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }
}

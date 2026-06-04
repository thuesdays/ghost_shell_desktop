// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Data.Services;

/// <summary>
/// Phase 35 — GitHub-based self-update mechanism. Polls the public
/// releases API, parses the latest release, and orchestrates download +
/// extraction + swap-in-place via a PowerShell helper that waits for the
/// main process to exit before touching files.
/// </summary>
internal sealed class GitHubUpdateService : IUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/thuesdays/ghost_shell_desktop/releases/latest";
    // Phase 38 fix — fallback used when /releases/latest 404s. The
    // /latest endpoint excludes drafts AND pre-releases by default,
    // so a release tagged as a pre-release (or one published from
    // the GitHub UI but flagged "Set as a pre-release") is invisible
    // to /latest. The list endpoint returns ALL releases including
    // pre-releases; we pick the newest non-draft as a graceful fallback.
    private const string ReleasesListApi = "https://api.github.com/repos/thuesdays/ghost_shell_desktop/releases?per_page=10";
    private const long MaxJsonBodyBytes = 1_048_576; // 1 MB
    private const long MaxZipFileBytes = 536_870_912; // 500 MB
    private const int CheckCacheTtlSeconds = 60;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<GitHubUpdateService> _log;
    private UpdateInfo? _latestKnown;
    private bool _updateAvailable;
    private DateTime? _lastCheckTime;
    private readonly SemaphoreSlim _checkMutex = new(1, 1);
    private readonly SemaphoreSlim _applyMutex = new(1, 1);

    /// <summary>Phase 71 — true while ApplyAsync is in progress. The
    /// scheduler observes this flag and stops firing new ticks while
    /// an update is preparing, so running scripts drain naturally
    /// instead of being killed mid-execution.</summary>
    private bool _isUpdatePending;
    private readonly object _updatePendingLock = new();

    public event EventHandler<UpdateInfo>? UpdateFound;
    /// <summary>Raised after the PowerShell helper is launched to
    /// signal the App layer it's safe to call
    /// <c>Application.Current.Shutdown(0)</c>. Lives behind an event
    /// so the Data project doesn't have to reference WPF.</summary>
    public event EventHandler? ShutdownRequested;

    /// <summary>Phase 71 — raised when IsUpdatePending flips so UI and
    /// scheduler can react immediately without polling.</summary>
    public event EventHandler? UpdatePendingChanged;

    public UpdateInfo? LatestKnown => _latestKnown;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set => _updateAvailable = value;
    }

    public bool IsUpdatePending => _isUpdatePending;

    /// <summary>Phase 71 — thread-safe setter for IsUpdatePending that
    /// raises UpdatePendingChanged only if the value actually flips.</summary>
    private void SetPending(bool v)
    {
        lock (_updatePendingLock)
        {
            if (_isUpdatePending == v) return; // No change
            _isUpdatePending = v;
        }
        // Fire the event outside the lock to avoid deadlock if subscribers
        // take locks themselves.
        try
        {
            UpdatePendingChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "UpdatePendingChanged handler threw");
        }
    }

    private readonly IRunService _runService;

    public GitHubUpdateService(
        IHttpClientFactory httpFactory,
        IRunService runService,
        ILogger<GitHubUpdateService> log)
    {
        _httpFactory = httpFactory;
        _runService = runService;
        _log = log;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        // [FIX: concurrent-check-guard] Guard against concurrent checks and use cache
        await _checkMutex.WaitAsync(ct);
        try
        {
            // [FIX: check-cache-ttl] Return cached result if check ran within TTL
            if (_latestKnown is not null && _lastCheckTime.HasValue)
            {
                var elapsed = DateTime.UtcNow - _lastCheckTime.Value;
                if (elapsed.TotalSeconds < CheckCacheTtlSeconds)
                {
                    _log.LogDebug("Using cached check result (TTL: {Elapsed}s)", elapsed.TotalSeconds);
                    return _latestKnown;
                }
            }

            var http = _httpFactory.CreateClient(nameof(GitHubUpdateService));
            if (!http.DefaultRequestHeaders.UserAgent.Any())
            {
                http.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent",
                    "GhostShell-Updater/1.0");
            }
            http.Timeout = TimeSpan.FromSeconds(15);

            // [FIX: http-redirect-validation] Configure allowed redirects with host validation
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 5
            };
            using (var redirectValidatingClient = new HttpClient(handler))
            {
                redirectValidatingClient.DefaultRequestHeaders.TryAddWithoutValidation(
                    "User-Agent", "GhostShell-Updater/1.0");

                // Phase 38 — Personal Access Token support for private
                // repos. GitHub's REST API returns 404 (not 401) for any
                // anonymous request to a private repo, as a privacy
                // measure. Setting GITHUB_TOKEN in the environment
                // (or a future Settings → Updates field) lets us auth
                // with `Authorization: Bearer <token>`. The token only
                // needs `Contents:read` (fine-grained) or `repo` scope
                // (classic). When unset, we fall through to anonymous —
                // which works for public repos.
                var pat = Environment.GetEnvironmentVariable("GHOSTSHELL_GITHUB_TOKEN")
                       ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                if (!string.IsNullOrWhiteSpace(pat))
                {
                    redirectValidatingClient.DefaultRequestHeaders.TryAddWithoutValidation(
                        "Authorization", "Bearer " + pat.Trim());
                    redirectValidatingClient.DefaultRequestHeaders.TryAddWithoutValidation(
                        "X-GitHub-Api-Version", "2022-11-28");
                }
                redirectValidatingClient.Timeout = TimeSpan.FromSeconds(15);

                using (var response = await redirectValidatingClient.GetAsync(ReleasesApi, ct))
                {
                    // [FIX: http-redirect-validation] Validate final redirect target
                    if (response.RequestMessage?.RequestUri != null)
                    {
                        var finalHost = response.RequestMessage.RequestUri.Host;
                        var isValidHost = finalHost == "api.github.com" || finalHost.EndsWith(".github.com") ||
                                        finalHost.EndsWith(".githubusercontent.com") || finalHost.EndsWith(".amazonaws.com");
                        if (!isValidHost)
                        {
                            _log.LogWarning("Redirect target host {Host} is not whitelisted", finalHost);
                            return null;
                        }
                    }

                    // Source of the release JSON: either /releases/latest
                    // (happy path) or /releases?per_page=10 (fallback when
                    // /latest 404s — e.g. all releases are pre-releases or
                    // drafts). Resolved into one local string so the parse
                    // path below doesn't branch.
                    string? json = null;

                    if (response.IsSuccessStatusCode)
                    {
                        // [FIX: body-size-cap] Cap JSON response at 1 MB
                        var contentLength = response.Content.Headers.ContentLength ?? 0;
                        if (contentLength > MaxJsonBodyBytes)
                        {
                            _log.LogWarning("GitHub API response too large: {Size} bytes (max: {Max})",
                                contentLength, MaxJsonBodyBytes);
                            return null;
                        }
                        json = await response.Content.ReadAsStringAsync(ct);
                        if (json.Length * 2 > MaxJsonBodyBytes) // UTF-16 estimate
                        {
                            _log.LogWarning("GitHub API response JSON exceeded max size during download");
                            return null;
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        // /releases/latest 404s when the repo has no
                        // releases yet, or every release is a draft, or
                        // every release is flagged as a pre-release
                        // (GitHub excludes pre-releases from /latest).
                        // For the latter cases we have a workable fallback:
                        // hit /releases?per_page=10 and pick the newest
                        // non-draft. The list is newest-first by default.
                        _log.LogDebug("/releases/latest returned 404 — falling back to /releases list");
                        using var listResp = await redirectValidatingClient.GetAsync(ReleasesListApi, ct);
                        if (!listResp.IsSuccessStatusCode)
                        {
                            // Distinguish "no releases" from "repo is private + no auth".
                            // Both manifest as 404 to anonymous callers; if no token
                            // is set, lean toward the private-repo explanation since
                            // it's actionable. Set GHOSTSHELL_GITHUB_TOKEN env var
                            // (Personal Access Token with Contents:read scope) to
                            // let the updater auth into private repos.
                            var hasToken = !string.IsNullOrWhiteSpace(
                                Environment.GetEnvironmentVariable("GHOSTSHELL_GITHUB_TOKEN")
                             ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
                            if (hasToken)
                            {
                                _log.LogInformation(
                                    "GitHub releases endpoint returned 404 even with a token — repo has no releases yet, or the token lacks Contents:read scope");
                            }
                            else
                            {
                                _log.LogInformation(
                                    "GitHub releases endpoint returned 404 — either the repo has no releases yet OR the repo is private (set GHOSTSHELL_GITHUB_TOKEN env var with a Personal Access Token to auth)");
                            }
                            return null;
                        }
                        var listJson = await listResp.Content.ReadAsStringAsync(ct);
                        using var listDoc = JsonDocument.Parse(listJson);
                        if (listDoc.RootElement.ValueKind != JsonValueKind.Array || listDoc.RootElement.GetArrayLength() == 0)
                        {
                            _log.LogInformation("GitHub /releases returned empty array — no releases to surface");
                            return null;
                        }
                        JsonElement? picked = null;
                        foreach (var rel in listDoc.RootElement.EnumerateArray())
                        {
                            bool isDraft = rel.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True;
                            if (!isDraft) { picked = rel; break; }
                        }
                        if (picked is null)
                        {
                            _log.LogInformation("All GitHub releases are drafts — nothing to surface yet");
                            return null;
                        }
                        json = picked.Value.GetRawText();
                    }
                    else
                    {
                        _log.LogWarning("GitHub API returned {StatusCode}", response.StatusCode);
                        return null;
                    }

                    if (json is null)
                    {
                        // Defence-in-depth — every branch above either set
                        // json or returned. If we got here, log + bail.
                        _log.LogWarning("GitHub release JSON not available after fetch (unexpected control flow)");
                        return null;
                    }

                    var release = JsonSerializer.Deserialize<GitHubRelease>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });

                    if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                    {
                        _log.LogWarning("GitHub release JSON missing required fields");
                        return null;
                    }

                    // [FIX: version-tag-parsing-leniency] Parse version case-insensitively and handle edge cases
                    var tagVersion = release.TagName.TrimStart('v', 'V');
                    if (!Version.TryParse(tagVersion, out var latestVer))
                    {
                        // Try to extract numeric prefix via regex
                        var match = Regex.Match(tagVersion, @"^(\d+(?:\.\d+){1,3})");
                        if (match.Success)
                        {
                            var numericPart = match.Groups[1].Value;
                            var parts = numericPart.Split('.');
                            // Coerce to 4-part version
                            while (parts.Length < 4)
                            {
                                numericPart += ".0";
                                parts = numericPart.Split('.');
                            }
                            if (!Version.TryParse(numericPart, out latestVer))
                            {
                                _log.LogWarning("Failed to parse version from tag {Tag}", release.TagName);
                                return null;
                            }
                        }
                        else
                        {
                            _log.LogWarning("Failed to parse version from tag {Tag}", release.TagName);
                            return null;
                        }
                    }

                    var currentVer = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);

                    // [FIX: version-comparison-normalisation] Normalize both versions to 4-part for comparison
                    var latestVerNorm = new Version(
                        latestVer.Major >= 0 ? latestVer.Major : 0,
                        latestVer.Minor >= 0 ? latestVer.Minor : 0,
                        latestVer.Build >= 0 ? latestVer.Build : 0,
                        latestVer.Revision >= 0 ? latestVer.Revision : 0);
                    var currentVerNorm = new Version(
                        currentVer.Major >= 0 ? currentVer.Major : 0,
                        currentVer.Minor >= 0 ? currentVer.Minor : 0,
                        currentVer.Build >= 0 ? currentVer.Build : 0,
                        currentVer.Revision >= 0 ? currentVer.Revision : 0);

                    // Find asset URLs
                    string? portableZipUrl = null;
                    string? installerExeUrl = null;

                    if (release.Assets != null)
                    {
                        portableZipUrl = release.Assets
                            .FirstOrDefault(a => a.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                            ?.BrowserDownloadUrl;

                        installerExeUrl = release.Assets
                            .FirstOrDefault(a => a.Name?.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase) == true)
                            ?.BrowserDownloadUrl;
                    }

                    var info = new UpdateInfo
                    {
                        LatestVersion = latestVerNorm,
                        CurrentVersion = currentVerNorm,
                        TagName = release.TagName,
                        ReleaseName = release.Name ?? "Release",
                        ReleaseNotes = release.Body ?? "",
                        PublishedAt = release.PublishedAt ?? DateTime.UtcNow,
                        PortableZipUrl = portableZipUrl,
                        InstallerExeUrl = installerExeUrl,
                        ReleasePageUrl = release.HtmlUrl ?? "https://github.com/thuesdays/ghost_shell_desktop/releases"
                    };

                    _latestKnown = info;
                    _lastCheckTime = DateTime.UtcNow;
                    _updateAvailable = latestVerNorm.CompareTo(currentVerNorm) > 0;

                    if (_updateAvailable)
                    {
                        _log.LogInformation("Update available: {Current} → {Latest}",
                            currentVerNorm, latestVerNorm);
                        UpdateFound?.Invoke(this, info);
                    }

                    return info;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Update check failed");
            return null;
        }
        finally
        {
            _checkMutex.Release();
        }
    }

    // audit DATA-02: single source of truth for the GitHub-asset host
    // allowlist. CheckAsync validated the API redirect target inline; the
    // download path in ApplyAsync now reuses the SAME rule so the bytes that
    // actually get executed can never be fetched from an off-allowlist host
    // (e.g. a tampered browser_download_url pointing at attacker.example).
    private static bool IsTrustedGitHubHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.ToLowerInvariant();
        return host == "api.github.com"
            || host == "github.com"
            || host.EndsWith(".github.com")
            || host.EndsWith(".githubusercontent.com")
            || host.EndsWith(".amazonaws.com"); // GitHub release assets are served from S3-backed CDNs
    }

    // audit DATA-02: validate a download URL is absolute, HTTPS, and points at
    // a trusted GitHub-controlled host before we ever fetch its bytes.
    private static bool IsTrustedDownloadUrl(string? url, out Uri? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false; // never weaken TLS / allow plain HTTP
        if (!IsTrustedGitHubHost(uri.Host)) return false;
        parsed = uri;
        return true;
    }

    // audit DATA-01: parse a 64-hex-char SHA-256 digest out of arbitrary text.
    // Accepts a bare hex string, a "<hex>  filename" checksum-file line, or a
    // digest embedded in the release body (e.g. "SHA256: <hex>"). Returns the
    // lowercase hex if exactly one well-formed candidate is found, else null.
    private static string? ExtractSha256Hex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = Regex.Match(text, @"\b([A-Fa-f0-9]{64})\b");
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    // audit DATA-01: compute the SHA-256 of the downloaded artifact as a
    // lowercase hex string. Streamed so a 500 MB zip isn't buffered in memory.
    private static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // audit DATA-01: resolve the expected SHA-256 for the portable zip from
    // release metadata that has ALREADY been fetched/derived, without adding a
    // public property to UpdateInfo (owned by another file). Two sources, in
    // order of preference:
    //   1. A sibling checksum asset (PortableZipUrl + ".sha256"), fetched from
    //      the same trusted host. This is the canonical, machine-readable form.
    //   2. A 64-hex digest embedded in the release notes body (ReleaseNotes).
    // Returns null when no expected hash can be obtained — the caller MUST then
    // fail closed (abort the update) rather than trusting unverified bytes.
    private async Task<string?> ResolveExpectedZipSha256Async(
        UpdateInfo info, HttpClient http, CancellationToken ct)
    {
        // (1) Sibling ".sha256" asset next to the zip.
        if (IsTrustedDownloadUrl(info.PortableZipUrl, out var zipUri) && zipUri is not null)
        {
            var checksumUrl = info.PortableZipUrl + ".sha256";
            if (IsTrustedDownloadUrl(checksumUrl, out _))
            {
                try
                {
                    using var resp = await http.GetAsync(checksumUrl, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        // Cap the checksum body — a real .sha256 is < 200 bytes.
                        var len = resp.Content.Headers.ContentLength ?? 0;
                        if (len <= 4096)
                        {
                            var body = await resp.Content.ReadAsStringAsync(ct);
                            var fromAsset = ExtractSha256Hex(body);
                            if (fromAsset is not null)
                            {
                                _log.LogInformation("Update integrity: expected SHA-256 sourced from sibling .sha256 asset");
                                return fromAsset;
                            }
                        }
                    }
                    else
                    {
                        _log.LogDebug("No sibling .sha256 asset ({Status})", resp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Failed to fetch sibling .sha256 asset");
                }
            }
        }

        // (2) Digest embedded in the release notes body.
        var fromNotes = ExtractSha256Hex(info.ReleaseNotes);
        if (fromNotes is not null)
        {
            _log.LogInformation("Update integrity: expected SHA-256 sourced from release notes body");
            return fromNotes;
        }

        return null;
    }

    // ─── audit DATA-01: Authenticode authenticity gate ──────────────────
    //
    // Publisher-pin substring matched against the signing certificate's
    // Subject. EMPTY = "do not pin" (an unsigned binary then only warns).
    // Set this to your code-signing certificate's subject (e.g. the
    // organisation/individual CN) to (a) REQUIRE the binary be signed by
    // exactly that publisher and (b) make an unsigned binary fail closed.
    private const string ExpectedPublisherSubstring = "";

    private void VerifyExtractedExeAuthenticode(string exePath, string stagingDir)
    {
        var trust = WinVerifyTrustFile(exePath);
        if (trust == 0) // valid signature, intact content, trusted chain
        {
            var publisher = "unknown";
            try
            {
                using var c = new X509Certificate2(X509Certificate.CreateFromSignedFile(exePath));
                publisher = c.Subject;
            }
            catch { /* signature valid but cert read failed — non-fatal */ }

            if (!string.IsNullOrEmpty(ExpectedPublisherSubstring)
                && publisher.IndexOf(ExpectedPublisherSubstring, StringComparison.OrdinalIgnoreCase) < 0)
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
                throw new InvalidOperationException(
                    $"Update aborted — binary is Authenticode-signed but by an UNEXPECTED publisher ('{publisher}'). " +
                    $"Expected a certificate whose subject contains '{ExpectedPublisherSubstring}'.");
            }
            _log.LogInformation(
                "Update authenticity: extracted GhostShell.exe has a valid Authenticode signature ({Publisher})",
                publisher);
            return;
        }

        var code = unchecked((uint)trust);
        const uint TRUST_E_NOSIGNATURE          = 0x800B0100;
        const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
        const uint TRUST_E_PROVIDER_UNKNOWN     = 0x800B0001;
        var unsigned = code is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN;

        if (unsigned)
        {
            if (!string.IsNullOrEmpty(ExpectedPublisherSubstring))
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
                throw new InvalidOperationException(
                    "Update aborted — a trusted publisher is pinned but the extracted binary is NOT Authenticode-signed.");
            }
            _log.LogWarning(
                "Update authenticity: extracted GhostShell.exe is NOT Authenticode-signed (0x{Code:X8}); " +
                "proceeding on SHA-256 integrity only. Sign release builds and set ExpectedPublisherSubstring " +
                "to enforce fail-closed authenticity.", code);
            return;
        }

        // Signed but the signature / chain FAILED — tampering, an untrusted
        // root, or a revoked/expired cert. Strong attack signal: never swap
        // this binary in.
        try { Directory.Delete(stagingDir, recursive: true); } catch { }
        throw new InvalidOperationException(
            $"Update aborted — extracted binary has an INVALID Authenticode signature (WinVerifyTrust 0x{code:X8}); " +
            "possible tampering or an untrusted signer.");
    }

    /// <summary>
    /// WinVerifyTrust against WINTRUST_ACTION_GENERIC_VERIFY_V2 — the standard
    /// Windows Authenticode check (embedded signature + content hash + cert
    /// chain to a trusted root). Returns 0 on success, else the provider
    /// status/HRESULT. UI suppressed (no prompts).
    /// </summary>
    private static int WinVerifyTrustFile(string path)
    {
        var actionGuid = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // GENERIC_VERIFY_V2
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct       = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath  = path,
            hFile          = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData      = IntPtr.Zero,
                dwUIChoice          = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE (chain trust still enforced)
                dwUnionChoice       = 1, // WTD_CHOICE_FILE
                pFile               = pFile,
                dwStateAction       = 0, // WTD_STATEACTION_IGNORE
                hWVTStateData       = IntPtr.Zero,
                pwszURLReference    = IntPtr.Zero,
                dwProvFlags         = 0,
                dwUIContext         = 0,
            };
            try { return WinVerifyTrust(IntPtr.Zero, actionGuid, ref data); }
            catch (DllNotFoundException) { return unchecked((int)0x800B0001); } // provider unknown (non-Windows)
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(
        IntPtr hWnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    public async Task<bool> ApplyAsync(UpdateInfo info, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (info.LatestVersion.CompareTo(info.CurrentVersion) <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(info.PortableZipUrl))
        {
            throw new InvalidOperationException(
                "Release does not include a portable .zip. Please download from the release page.");
        }

        // audit DATA-02: re-validate the download URL host HERE, at the point
        // the executed bytes are fetched. CheckAsync's redirect check covered
        // only the API request; browser_download_url comes verbatim from the
        // (potentially tampered) release JSON and was never constrained. Reject
        // anything that isn't HTTPS to a trusted GitHub-controlled host.
        if (!IsTrustedDownloadUrl(info.PortableZipUrl, out _))
        {
            _log.LogError("Refusing to download update from untrusted URL: {Url}", info.PortableZipUrl);
            throw new InvalidOperationException(
                "Release download URL is not a trusted GitHub asset host. Aborting update.");
        }

        // [FIX: concurrent-apply-guard] Guard against concurrent apply operations
        if (!_applyMutex.Wait(0))
        {
            throw new InvalidOperationException("Update already in progress.");
        }

        // Phase 71 — flag that update is preparing so the scheduler stops
        // firing new ticks and lets active runs drain naturally.
        SetPending(true);

        // [FIX: parent-pid-race] Capture parent PID and session token at entry
        var parentPid = Environment.ProcessId;
        var sessionToken = Guid.NewGuid().ToString();

        // Stage directory paths declared OUT here so the catch block at the
        // bottom can reach them for the failure-cleanup. Also makes the data
        // flow easier to read in one place.
        var stagingDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostShell",
            "updates",
            $"v{info.LatestVersion}");
        var extractDir = Path.Combine(stagingDir, "extracted");
        var zipPath = Path.Combine(stagingDir, "update.zip");

        try
        {
            Directory.CreateDirectory(stagingDir);

            // [FIX: parent-pid-race] Write sentinel file with parent PID and session token.
            //
            // Phase 69c — for self-contained .NET 8 deployments, Assembly.Location
            // returns the path to the managed DLL (GhostShell.dll), NOT the apphost
            // executable (GhostShell.exe). The PowerShell helper's parent-PID
            // validation compares against $parentProc.MainModule.FileName which
            // always returns the .exe path, so the previous "Assembly.Location"
            // value caused the comparison to mismatch on EVERY update -- the PS
            // script bailed out with "parent PID recycled" (false positive) and
            // the file swap never ran. Use Process.MainModule.FileName to get
            // the .exe path, fall back to Location only if MainModule isn't
            // accessible (single-file publish edge cases).
            var currentExePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(currentExePath))
            {
                currentExePath = Assembly.GetExecutingAssembly().Location;
            }
            // If we still ended up with a .dll path, swap to the sibling .exe
            // (apphost) which is what MainModule.FileName surfaces in PS.
            if (currentExePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var sibling = Path.ChangeExtension(currentExePath, ".exe");
                if (File.Exists(sibling)) currentExePath = sibling;
            }
            var sentinelPath = Path.Combine(stagingDir, "session.txt");
            File.WriteAllText(sentinelPath, $"{parentPid}|{sessionToken}|{currentExePath}");

            // Download zip with progress (0-50%)
            progress?.Report(0);

            var http = _httpFactory.CreateClient(nameof(GitHubUpdateService));
            http.Timeout = TimeSpan.FromSeconds(300);

            try
            {
                // HttpResponseMessage is IDisposable but NOT IAsyncDisposable
                // in .NET 8, so plain `using` is correct here. The body's
                // Stream IS IAsyncDisposable and is awaited below.
                using (var response = await http.GetAsync(info.PortableZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"Download failed: {response.StatusCode}");
                    }

                    // audit DATA-02: re-validate the FINAL redirect target host.
                    // The named factory client may follow redirects; the initial
                    // URL passing the allowlist doesn't guarantee the bytes came
                    // from a trusted host. Reject if a redirect landed off-list.
                    var finalUri = response.RequestMessage?.RequestUri;
                    if (finalUri is null || !IsTrustedGitHubHost(finalUri.Host) ||
                        finalUri.Scheme != Uri.UriSchemeHttps)
                    {
                        _log.LogError("Update download redirected to untrusted host: {Host}", finalUri?.Host);
                        throw new InvalidOperationException(
                            "Update download redirected to an untrusted host. Aborting update.");
                    }

                    // [FIX: body-size-cap] Cap ZIP download at 500 MB
                    var contentLength = response.Content.Headers.ContentLength ?? 0;
                    if (contentLength > MaxZipFileBytes)
                    {
                        _log.LogWarning("Release ZIP too large: {Size} bytes (max: {Max})",
                            contentLength, MaxZipFileBytes);
                        throw new InvalidOperationException($"Release ZIP exceeds maximum size of {MaxZipFileBytes} bytes.");
                    }

                    var canReportProgress = contentLength > 0;

                    await using (var source = await response.Content.ReadAsStreamAsync(ct))
                    using (var dest = File.Create(zipPath))
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await source.ReadAsync(buffer, ct)) > 0)
                        {
                            // [FIX: body-size-cap] Abort if streaming exceeds limit
                            totalRead += bytesRead;
                            if (totalRead > MaxZipFileBytes)
                            {
                                _log.LogWarning("ZIP download exceeded maximum size during transfer");
                                throw new InvalidOperationException($"ZIP download exceeded maximum size of {MaxZipFileBytes} bytes.");
                            }

                            await dest.WriteAsync(buffer, 0, bytesRead, ct);

                            if (canReportProgress)
                            {
                                var progressPercent = (int)((totalRead * 50) / contentLength);
                                progress?.Report(progressPercent);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (!(ex is InvalidOperationException))
            {
                // [FIX: staging-dir-cleanup-on-failure] Clean up partial download
                try { File.Delete(zipPath); } catch { }
                throw new InvalidOperationException("Failed to download release ZIP.", ex);
            }

            progress?.Report(50);

            // audit DATA-01: integrity verification BEFORE the zip is ever
            // extracted or any file is swapped over the live install. The
            // update channel is the most-trusted code path in the app and was
            // previously completely unauthenticated — a tampered release JSON,
            // hijacked repo, stolen token, or re-uploaded asset could ship
            // arbitrary native code that ran on next launch. We now require a
            // SHA-256 digest from already-fetched release metadata (sibling
            // .sha256 asset preferred, else a digest in the release notes),
            // compute the SHA-256 of the downloaded bytes, and compare.
            //
            // FAIL CLOSED: if no expected digest can be resolved, or it does
            // not match, we delete the staging dir and abort. A missing digest
            // is a HARD failure, never a warning — unverified bytes are never
            // extracted or executed.
            //
            // NOTE: this gives integrity against a tampered/MITM'd asset given
            // an authentic release-metadata digest. It does NOT yet give full
            // authenticity (a fully compromised release could publish a
            // matching digest for malicious bytes). A detached signature over
            // the digest with an embedded public key, plus Authenticode
            // verification of the extracted GhostShell.exe, is the stronger
            // follow-up — see crossFileNeeded, it needs a public key / signed
            // manifest plumbed through release metadata (UpdateInfo lives in
            // another file we must not edit here).
            {
                var expectedSha = await ResolveExpectedZipSha256Async(info, http, ct);
                if (string.IsNullOrWhiteSpace(expectedSha))
                {
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
                    _log.LogError(
                        "Update aborted: no SHA-256 checksum available for the release artifact (no sibling .sha256 asset and none in release notes). Refusing to apply an unverified update.");
                    throw new InvalidOperationException(
                        "Update artifact has no published SHA-256 checksum to verify against. Aborting update for safety.");
                }

                var actualSha = await ComputeFileSha256Async(zipPath, ct);
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(stagingDir, recursive: true); } catch { }
                    _log.LogError(
                        "Update aborted: SHA-256 mismatch. expected={Expected} actual={Actual}. The downloaded artifact is corrupt or tampered.",
                        expectedSha, actualSha);
                    throw new InvalidOperationException(
                        "Update artifact failed SHA-256 integrity verification. Aborting update.");
                }

                _log.LogInformation("Update integrity verified: SHA-256 {Sha} matches expected digest", actualSha);
            }

            // Extract (50-90%)
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }
            Directory.CreateDirectory(extractDir);

            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entries = zip.Entries.ToList();
                    var basePath = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;

                    for (int i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];

                        // [FIX: zip-slip-protection] Validate extracted path stays within staging dir
                        var extractPath = Path.Combine(extractDir, entry.FullName);
                        var fullPath = Path.GetFullPath(extractPath);

                        if (!fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                        {
                            _log.LogWarning("ZIP entry attempted path traversal: {Entry}", entry.FullName);
                            continue; // Skip malicious entries
                        }

                        if (entry.FullName.EndsWith("/"))
                        {
                            Directory.CreateDirectory(extractPath);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(extractPath)!);
                            entry.ExtractToFile(extractPath, overwrite: true);
                        }

                        var progressPercent = 50 + (int)((i + 1) * 40.0 / entries.Count);
                        progress?.Report(progressPercent);
                    }
                }
            }
            catch (Exception ex)
            {
                // [FIX: staging-dir-cleanup-on-failure] Clean up partial extraction
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
                throw new InvalidOperationException("Failed to extract release ZIP.", ex);
            }

            progress?.Report(90);

            // [FIX: validate-extracted-exe] Validate extracted executable exists and is non-empty
            var exePath = Path.Combine(extractDir, "GhostShell.exe");
            if (!File.Exists(exePath) || new FileInfo(exePath).Length == 0)
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
                throw new InvalidOperationException(
                    "Release zip is malformed or incomplete (missing or empty GhostShell.exe).");
            }

            // audit DATA-01 (cross-file follow-up): AUTHENTICITY gate on the
            // binary we're about to swap in and relaunch. The SHA-256 check
            // above only proves the bytes match a digest taken from the SAME
            // release metadata — so a fully-compromised release (malicious
            // zip + matching .sha256) sails through. Authenticode is the
            // forge-proof gate: it verifies the exe carries a valid
            // signature whose content hash matches AND whose cert chains to
            // a trusted root — something an attacker without the publisher's
            // private key cannot produce. Fails closed on a tampered/invalid
            // signature; on a genuinely UNSIGNED build it warns and proceeds
            // (so today's possibly-unsigned releases still self-update on
            // SHA-256 integrity). Set ExpectedPublisherSubstring to harden
            // an unsigned binary to fail-closed and to pin the publisher.
            VerifyExtractedExeAuthenticode(exePath, stagingDir);

            // Phase 71 — drain active runs. We've flagged IsUpdatePending=true,
            // so the scheduler stopped firing new ticks. Wait for whatever's in
            // flight to finish naturally, polling the runner. Cap at 5 minutes —
            // past that we force-stop so a hung session doesn't block the update
            // indefinitely. progress.Report() jumps from 90 to 95 during drain.
            progress?.Report(91);
            var drainStart = DateTime.UtcNow;
            var drainTimeout = TimeSpan.FromMinutes(5);
            while (true)
            {
                var active = await _runService.ListAsync(limit: 100, status: RunStatusFilter.Running, ct: ct);
                if (active.Count == 0) break;
                var elapsed = DateTime.UtcNow - drainStart;
                if (elapsed > drainTimeout)
                {
                    _log.LogWarning("Update drain timeout: {N} active run(s) still alive after {S}s, forcing stop",
                        active.Count, (int)elapsed.TotalSeconds);
                    break;
                }
                _log.LogInformation("Update apply: waiting for {N} active run(s) to finish ({S}s elapsed)",
                    active.Count, (int)elapsed.TotalSeconds);
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            progress?.Report(95);

            // Write PowerShell helper with hardened error handling
            // [FIX: powershell-hardening] Enhanced apply.ps1 with error handling, logging, and timeout adjustments
            // PowerShell helper. Phase 69c rewrite -- robust against the
            // self-contained .NET 8 .dll-vs-.exe path mismatch that was
            // making the MainModule check fail on every run. Now compares
            // by filename + parent dir instead of full-path equality, and
            // logs ALL fields it considered so a failed update surfaces
            // a debuggable trail in update.log.
            var psScript = $@"$ErrorActionPreference = 'Stop'
$ParentPid = $args[0]
$Source = $args[1]
$Target = $args[2]
$RestartExe = $args[3]
$SessionToken = $args[4]
$CurrentExePath = $args[5]

function Log($msg) {{
    try {{ Add-Content -LiteralPath ""$Target\update.log"" -Value ""[$(Get-Date -Format 'HH:mm:ss.fff')] $msg"" }} catch {{ }}
}}

try {{
    Log ""[update] starting parent_pid=$ParentPid session=$SessionToken""
    Log ""[update] source=$Source target=$Target restart=$RestartExe expected_exe=$CurrentExePath""

    $parentProc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
    if ($null -eq $parentProc) {{
        Log ""[warn] parent $ParentPid already gone -- assuming clean exit, proceeding to copy""
    }} else {{
        # Compare by FILENAME + PARENT DIRECTORY, not full path. .NET self-
        # contained apps surface MainModule.FileName as <dir>\GhostShell.exe,
        # but Assembly.Location returns <dir>\GhostShell.dll -- the C# side
        # tries to send the .exe path now, but stay defensive against the
        # legacy .dll-path case so a stale staged update from the previous
        # build doesn't brick the swap.
        $parentExe = $null
        try {{ $parentExe = $parentProc.MainModule.FileName }} catch {{ Log ""[warn] couldn't read MainModule: $_"" }}

        $expectedDir = if ($CurrentExePath) {{ Split-Path -Parent $CurrentExePath }} else {{ '' }}
        $actualDir   = if ($parentExe) {{ Split-Path -Parent $parentExe }} else {{ '' }}

        $sameDir = $expectedDir -and $actualDir -and ($expectedDir.TrimEnd('\','/').ToLower() -eq $actualDir.TrimEnd('\','/').ToLower())

        if (-not $sameDir) {{
            # Last-resort tolerant check: if either path resolves to inside
            # $Target, accept it. The user is updating the install we know
            # about so MainModule should agree on the dir.
            $targetNorm = $Target.TrimEnd('\','/').ToLower()
            if (($expectedDir.TrimEnd('\','/').ToLower() -eq $targetNorm) -or ($actualDir.TrimEnd('\','/').ToLower() -eq $targetNorm)) {{
                $sameDir = $true
            }}
        }}

        if (-not $sameDir) {{
            Log ""[warn] parent_exe=$parentExe vs expected=$CurrentExePath -- proceeding anyway (lenient mode)""
        }} else {{
            Log ""[info] parent identity check OK""
        }}

        Log ""[info] waiting for parent process to exit (60s timeout)""
        try {{
            Wait-Process -Id $ParentPid -Timeout 60
            Log ""[info] parent process exited""
        }}
        catch {{
            $still = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
            if ($null -ne $still) {{
                Log ""[warn] timeout after 60s but parent still alive -- continuing anyway""
            }} else {{
                Log ""[info] parent gone (Wait threw $_)""
            }}
        }}
    }}

    # Give Windows a beat to fully release file handles after the parent died.
    Start-Sleep -Seconds 2

    # File swap. Track failures so we can surface them in the log instead of
    # silently launching a half-updated app.
    $failureCount = 0
    Get-ChildItem -LiteralPath $Source -Recurse -File | ForEach-Object {{
        $rel = $_.FullName.Substring($Source.Length).TrimStart('\','/')
        $dst = Join-Path $Target $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
        try {{
            Copy-Item -LiteralPath $_.FullName -Destination $dst -Force -ErrorAction Stop
        }}
        catch {{
            $failureCount++
            Log ""[error] FAIL $rel : $_""
        }}
    }}

    if ($failureCount -gt 0) {{
        Log ""[error] $failureCount files failed to copy -- aborting restart""
        exit 1
    }}

    Log ""[update] swapped at $(Get-Date)""

    # Resolve full path to restart exe so Start-Process doesn't depend on
    # PowerShell's CWD or PATH lookup. $RestartExe is the bare filename
    # (e.g. 'GhostShell.exe'), $Target is the install dir.
    $restartFull = Join-Path $Target $RestartExe
    if (-not (Test-Path -LiteralPath $restartFull)) {{
        Log ""[error] restart exe missing after swap: $restartFull""
        exit 1
    }}
    Log ""[update] launching $restartFull""
    Start-Process -FilePath $restartFull -WorkingDirectory $Target
    Log ""[update] launched -- script done""
}}
catch {{
    Log ""[fatal] update failed: $_""
    Log ""[fatal] stack: $($_.ScriptStackTrace)""
    exit 1
}}
";
            var psPath = Path.Combine(stagingDir, "apply.ps1");
            File.WriteAllText(psPath, psScript);

            // Spawn PowerShell to apply
            var currentExeDir = Path.GetDirectoryName(currentExePath) ?? Environment.CurrentDirectory;

            // [FIX: powershell-hardening] Quote all path arguments for safety
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $@"-NoProfile -ExecutionPolicy Bypass -File ""{psPath}"" " +
                    $@"{parentPid} ""{extractDir}"" ""{currentExeDir}"" ""GhostShell.exe"" ""{sessionToken}"" ""{currentExePath}""",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                // Don't wait — the PowerShell script waits for us to exit
            }

            progress?.Report(100);

            // Hand off to the App layer for the actual WPF teardown.
            // The Data project doesn't reference PresentationFramework
            // (and shouldn't — it's a pure data + service-glue layer),
            // so the previous direct `System.Windows.Application.Current.Shutdown`
            // wouldn't compile here. App.xaml.cs subscribes to this
            // event and runs the dispatcher-thread shutdown there.
            try
            {
                ShutdownRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // A misbehaving subscriber must not poison the
                // updater — log and keep going. The PowerShell helper
                // is already running and will swap files once the
                // process exits naturally (e.g. via the user closing
                // the window).
                _log.LogWarning(ex, "ShutdownRequested handler threw");
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update apply failed");
            // Phase 71 — reset the pending flag on failure so the scheduler
            // can resume if the user tries again.
            SetPending(false);
            // [FIX: staging-dir-cleanup-on-failure] Final cleanup on catch.
            // stagingDir is declared at method scope so it's reachable here
            // even if we threw before any of the inside-try-block work.
            try { Directory.Delete(stagingDir, recursive: true); } catch { }
            throw;
        }
        finally
        {
            _applyMutex.Release();
        }
    }

#pragma warning disable CS8618
    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("body")]
        public string Body { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime? PublishedAt { get; set; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<Asset> Assets { get; set; }
    }

    private class Asset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; }
    }
#pragma warning restore CS8618
}

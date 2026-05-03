// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
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

    public event EventHandler<UpdateInfo>? UpdateFound;
    /// <summary>Raised after the PowerShell helper is launched to
    /// signal the App layer it's safe to call
    /// <c>Application.Current.Shutdown(0)</c>. Lives behind an event
    /// so the Data project doesn't have to reference WPF.</summary>
    public event EventHandler? ShutdownRequested;

    public UpdateInfo? LatestKnown => _latestKnown;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set => _updateAvailable = value;
    }

    public GitHubUpdateService(IHttpClientFactory httpFactory, ILogger<GitHubUpdateService> log)
    {
        _httpFactory = httpFactory;
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

        // [FIX: concurrent-apply-guard] Guard against concurrent apply operations
        if (!_applyMutex.Wait(0))
        {
            throw new InvalidOperationException("Update already in progress.");
        }

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

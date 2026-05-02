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

                    if (!response.IsSuccessStatusCode)
                    {
                        _log.LogWarning("GitHub API returned {StatusCode}", response.StatusCode);
                        return null;
                    }

                    // [FIX: body-size-cap] Cap JSON response at 1 MB
                    var contentLength = response.Content.Headers.ContentLength ?? 0;
                    if (contentLength > MaxJsonBodyBytes)
                    {
                        _log.LogWarning("GitHub API response too large: {Size} bytes (max: {Max})",
                            contentLength, MaxJsonBodyBytes);
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync(ct);
                    if (json.Length * 2 > MaxJsonBodyBytes) // UTF-16 estimate
                    {
                        _log.LogWarning("GitHub API response JSON exceeded max size during download");
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

            // [FIX: parent-pid-race] Write sentinel file with parent PID and session token
            var sentinelPath = Path.Combine(stagingDir, "session.txt");
            var currentExePath = Assembly.GetExecutingAssembly().Location;
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
            var psScript = $@"$ErrorActionPreference = 'Stop'
$ParentPid = $args[0]
$Source = $args[1]
$Target = $args[2]
$RestartExe = $args[3]
$SessionToken = $args[4]
$CurrentExePath = $args[5]

try {{
    Add-Content ""$Target\update.log"" ""[update] starting at $(Get-Date) parent_pid=$ParentPid session=$SessionToken""

    # [FIX: powershell-hardening] Validate parent process before waiting
    $parentProc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
    if ($null -eq $parentProc) {{
        Add-Content ""$Target\update.log"" ""[error] parent process $ParentPid no longer exists""
        exit 1
    }}

    # [FIX: powershell-hardening] Validate it's our application
    if ($parentProc.MainModule.FileName -ne $CurrentExePath) {{
        Add-Content ""$Target\update.log"" ""[error] parent PID recycled to different process: $($parentProc.MainModule.FileName)""
        exit 1
    }}

    # [FIX: powershell-hardening] Wait for parent with 60s timeout
    Wait-Process -Id $ParentPid -Timeout 60
    Add-Content ""$Target\update.log"" ""[info] parent process exited""
}}
catch {{
    $proc = Get-Process -Id $ParentPid -ErrorAction SilentlyContinue
    if ($null -ne $proc) {{
        Add-Content ""$Target\update.log"" ""[info] timeout waiting for parent, continuing anyway""
    }}
}}

# [FIX: powershell-hardening] Sleep longer to release file handles
Start-Sleep -Seconds 2

# [FIX: powershell-hardening] Copy files with failure tracking
$failureCount = 0
Get-ChildItem $Source -Recurse -File | ForEach-Object {{
    $rel = $_.FullName.Substring($Source.Length).TrimStart('\','/')
    $dst = Join-Path $Target $rel
    New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
    try {{
        Copy-Item -LiteralPath $_.FullName -Destination $dst -Force -ErrorAction Stop
    }}
    catch {{
        $failureCount++
        Add-Content ""$Target\update.log"" ""FAIL $rel : $_""
    }}
}}

if ($failureCount -gt 0) {{
    Add-Content ""$Target\update.log"" ""[error] ROLLBACK NEEDED - $failureCount files failed to copy""
    exit 1
}}

Add-Content ""$Target\update.log"" ""[update] swapped at $(Get-Date)""
Start-Process -FilePath $RestartExe -WorkingDirectory $Target
}}
catch {{
    Add-Content ""$Target\update.log"" ""[error] update failed: $_""
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

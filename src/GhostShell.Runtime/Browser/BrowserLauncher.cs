// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Generic;
using System.Diagnostics;
using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;
using OpenQA.Selenium.Chrome;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Real Selenium-backed browser launcher. Resolves Chromium via
/// <see cref="IChromiumLocator"/>, looks up the assigned proxy
/// (if any), composes ChromeOptions, spawns chromedriver, and
/// returns a <see cref="SeleniumBrowserSession"/> the caller drives
/// from there.
///
/// Phase 4 adds the auth-proxy sidecar: when the upstream proxy
/// URL has <c>user:pass@</c> AND the scheme is HTTP/HTTPS, we spin
/// up a local <see cref="IProxyAuthForwarder"/>, hand Chromium the
/// credential-free local URL, and let the forwarder inject
/// <c>Proxy-Authorization</c> on the wire. SOCKS5 with creds keeps
/// passing through directly — Chromium handles SOCKS auth natively.
/// </summary>
public sealed class BrowserLauncher : IBrowserLauncher
{
    private readonly IChromiumLocator _locator;
    private readonly IProxyService _proxies;
    private readonly IProxyAuthForwarderFactory _forwarderFactory;
    private readonly IExtensionService? _extensions;
    private readonly ITrafficService? _traffic;
    private readonly ISettingsService? _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<BrowserLauncher> _log;

    // Phase 71oo — per-profile launch gate. Prevents the
    // "DevToolsActivePort file doesn't exist" failure caused by two
    // concurrent LaunchAsync calls for the same profile killing each
    // other's chrome in the preflight orphan-reaper. See the class doc
    // on ProfileLaunchGate for the full story.
    private readonly ProfileLaunchGate _launchGate = new();

    public BrowserLauncher(
        IChromiumLocator locator,
        IProxyService proxies,
        IProxyAuthForwarderFactory forwarderFactory,
        ILoggerFactory loggerFactory,
        IExtensionService? extensions = null,
        ITrafficService? traffic = null,
        ISettingsService? settings = null)
    {
        _locator          = locator;
        _proxies          = proxies;
        _forwarderFactory = forwarderFactory;
        _extensions       = extensions;
        _traffic          = traffic;
        _settings         = settings;
        _loggerFactory    = loggerFactory;
        _log              = loggerFactory.CreateLogger<BrowserLauncher>();
    }

    public async Task<IBrowserSession> LaunchAsync(
        Profile profile, CancellationToken ct = default)
    {
        // Phase 71oo — acquire the per-profile launch gate BEFORE any
        // real work (locator / proxy / forwarder). Throws
        // ProfileBusyException if another LaunchAsync is mid-flight
        // for the same profile. Released by the `using` Dispose when
        // this method returns (success OR failure), at which point
        // the session is fully constructed and any subsequent launch
        // that intentionally replaces this one will preflight-kill
        // its chrome — which is the correct behaviour for a fresh
        // launch replacing a leftover session.
        using var gate = _launchGate.Acquire(profile.Name);

        // _locator.Locate() walks the filesystem looking for chrome.exe
        // / chromedriver.exe. On a cold disk this can take 100-300ms.
        // Wrap in Task.Run so it doesn't block the UI thread when
        // LaunchAsync is called from a UI button (Probe in profile,
        // Run, etc.). Multiple call sites benefit — runtime code stays
        // unaware of who's calling it.
        var status = await Task.Run(() => _locator.Locate(), ct);
        if (!status.Found)
        {
            throw new InvalidOperationException(
                "Cannot launch browser — patched Chromium build was not located. " +
                $"Looked at: {string.Join(" | ", status.Candidates)}. " +
                "Set GHOSTSHELL_CHROMIUM_DIR or place chromium\\ next to GhostShell.exe.");
        }

        // Resolve template + proxy off-thread before any process work.
        var template  = DeviceTemplateCatalog.Find(profile.TemplateId)
                        ?? PickAutoTemplate();

        // Proxy is OPTIONAL — when the editor's proxy combo was left
        // at "(none)" the slug is null and we launch over the local
        // egress IP. ChromeOptionsBuilder maps no-proxy to an explicit
        // `--no-proxy-server` so Chromium ignores any system proxy
        // (which would otherwise leak corp/IT settings into runs).
        string? proxyUrl = null;
        // audit LAUNCH-01: carry the proxy's geo-probed exit country to the
        // options builder so the spoofed JS timezone matches the IP's
        // country (timezone-vs-IP coherence), not just the profile language.
        string? proxyCountryCode = null;
        if (!string.IsNullOrWhiteSpace(profile.ProxySlug))
        {
            var proxy = await _proxies.GetAsync(profile.ProxySlug, ct);
            proxyUrl = proxy?.Url;
            proxyCountryCode = proxy?.CountryCode;
            if (proxyUrl is null)
                _log.LogWarning(
                    "Profile '{Name}' references proxy '{Slug}' which is missing from DB " +
                    "— falling back to direct connection (local IP)",
                    profile.Name, profile.ProxySlug);
        }
        else
        {
            _log.LogDebug(
                "Profile '{Name}' has no proxy assigned — launching direct (local IP)",
                profile.Name);
        }

        // ─── Phase 4: auth-proxy sidecar ──────────────────────────
        // If the upstream URL is HTTP(S) AND has user:pass@ baked in,
        // start a local forwarder and hand Chromium its address
        // instead. SOCKS5+creds passes through unchanged — Chromium's
        // SOCKS stack supports auth natively. Bare proxies (no creds)
        // also pass through. The forwarder is owned by the session
        // and disposed alongside the driver.
        IProxyAuthForwarder? forwarder = null;
        var chromiumProxyUrl = proxyUrl;
        if (NeedsAuthForwarder(proxyUrl))
        {
            try
            {
                forwarder = _forwarderFactory.Create();
                chromiumProxyUrl = await forwarder.StartAsync(proxyUrl!, ct);
                _log.LogInformation(
                    "Auth-proxy forwarder bound for '{Name}': Chromium → {Local} → upstream",
                    profile.Name, chromiumProxyUrl);
            }
            catch (Exception ex)
            {
                // audit LAUNCH-07 / PROXY-05: FAIL CLOSED. The previous
                // behaviour stripped credentials and launched anyway,
                // which silently degraded an authenticated-proxy profile:
                //   • a schemed http://user:pass@host gets StripAuth'd and
                //     every upstream request 407s (and may fall through to
                //     a real-IP path), and
                //   • a bare "user:pass@host:port" is returned UNCHANGED by
                //     StripAuth (it only strips when '://' is present), so
                //     Chromium receives credentials in --proxy-server and
                //     dies silently at boot ("DevToolsActivePort file
                //     doesn't exist").
                // Either way the runner is handed a broken-but-"healthy"
                // session that cannot reach the network through its assigned
                // proxy and risks a real-IP leak. The proxy was REQUIRED
                // (it carried embedded creds), so we abort the launch with a
                // clear error instead of degrading. Dispose any partial
                // forwarder first so its listening socket / upstream
                // connection isn't leaked.
                _log.LogError(ex,
                    "Failed to start auth-proxy forwarder for '{Name}' — aborting launch " +
                    "rather than degrading to an unauthenticated proxy (would 407 or leak)",
                    profile.Name);
                if (forwarder is not null)
                {
                    try { await forwarder.DisposeAsync(); } catch { /* swallow */ }
                    forwarder = null;
                }
                throw new InvalidOperationException(
                    $"Cannot launch profile '{profile.Name}': the auth-proxy forwarder for its " +
                    "assigned credentialed proxy failed to start. Refusing to launch over an " +
                    "unauthenticated/credential-bearing proxy to avoid a broken session or " +
                    "real-IP leak.", ex);
            }
        }
        else if (!string.IsNullOrWhiteSpace(proxyUrl)
                 && proxyUrl.Contains('@', StringComparison.Ordinal))
        {
            // Has creds but isn't HTTP(S) — e.g. SOCKS5. Pass through
            // unchanged; Chromium handles SOCKS auth on its own.
            _log.LogDebug(
                "Proxy '{Url}' has creds but is non-HTTP — letting Chromium handle auth natively",
                ChromeOptionsBuilder.StripAuth(proxyUrl));
        }

        _log.LogInformation(
            "Launching profile '{Name}' (template={Tpl}, proxy={Proxy}, chrome={Chrome})",
            profile.Name, template.Id, chromiumProxyUrl ?? "—", status.ChromePath);

        // ─── PREFLIGHT ────────────────────────────────────────────
        // Reap any orphan chrome/chromedriver still holding this
        // profile's --user-data-dir from a previous failed launch,
        // wipe stale Chrome session-restore state, and clear the
        // singleton-lock files. Without this step the new chrome.exe
        // crashes during boot because the dir is "in use", and
        // Selenium reports "DevToolsActivePort file doesn't exist"
        // — the canonical Selenium-says-this-but-means-something-
        // else error that ate two debug sessions before we wrote it.
        var userDataDir = AppPaths.ProfileDir(profile.Name);
        if (OperatingSystem.IsWindows())
            LaunchPreflight.Run(userDataDir, profile.Name, _log);

        // ─── Phase 71nn: heal corrupted Chrome JSON state ─────────
        // Cheap pre-launch sweep: stat Local State + Default/Preferences
        // + Default/Secure Preferences and quarantine any that are
        // 0 bytes or unparseable JSON. Without this the patched Chrome
        // exits instantly during boot and we get
        //   "session not created — cannot parse internal JSON template:
        //    EOF while parsing a value at line 1 column 0 (SessionNotCreated)"
        // which retries every 30 s forever (back-off ceiling = 3600 s
        // but RunnerHost still re-fires on the back-off cadence). Three
        // tiny file probes per launch — negligible cost on the hot path.
        ChromeProfileHealer.HealProfile(userDataDir, _log);

        // Phase 27 — gather the extensions the profile should load.
        // The service is optional so this launcher remains usable in
        // older test rigs that don't wire IExtensionService.
        IReadOnlyList<string>? extPaths = null;
        if (_extensions is not null)
        {
            try
            {
                var enabled = await _extensions.ListEnabledForProfileAsync(profile.Name, ct);
                extPaths = enabled.Select(e => e.LocalPath).ToList();
                if (extPaths.Count > 0)
                {
                    _log.LogInformation(
                        "Loading {Count} extension(s) for profile '{Name}': {Names}",
                        extPaths.Count, profile.Name,
                        string.Join(", ", enabled.Select(e => e.Name)));
                    // Register every extension in Default/Preferences:
                    //   • settings[id] = { location:4, state:1, … }
                    //     so Chrome 138+ doesn't auto-disable it as
                    //     an unverified sideload,
                    //   • pinned_actions / pinned_extensions / toolbar
                    //     so the toolbar button is visible immediately
                    //     instead of buried in the puzzle-piece menu,
                    //   • alerts.initialized = true to skip the first-
                    //     launch "you have new extensions" balloon.
                    ExtensionPinWriter.RegisterAndPin(profile.Name, enabled, _log);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Couldn't resolve extensions for profile '{Name}' — launching without extensions",
                    profile.Name);
            }
        }

        var options = ChromeOptionsBuilder.Build(
            profile, template, status.ChromePath!, chromiumProxyUrl, extPaths,
            proxyCountryCode); // audit LAUNCH-01: timezone follows the proxy exit country

        // ChromeDriverService takes the directory + filename so we can
        // ship a chromedriver.exe with a non-default name (vendored
        // alongside the patched chrome). We don't, but the API needs
        // both halves regardless.
        var driverDir = Path.GetDirectoryName(status.ChromeDriverPath!) ?? "";
        var driverExe = Path.GetFileName(status.ChromeDriverPath!);
        var service   = ChromeDriverService.CreateDefaultService(driverDir, driverExe);
        service.HideCommandPromptWindow = true;

        // Why we DON'T set SuppressInitialDiagnosticInformation:
        // that flag adds `--silent` to chromedriver's argv, and the
        // patched 149.0.7805.0 build interprets `--silent` strictly —
        // it suppresses not just the startup banner but also the
        // HTTP /status responses Selenium polls during service
        // bring-up. Selenium times out → "Cannot start the driver
        // service on http://localhost:NNNN/" with an empty log
        // file. Standalone (`chromedriver --port=9515`) works fine
        // precisely because no `--silent` was passed. Reproduced on
        // 2026-04-29; keep the line removed.
        //
        // Bump the init timeout from Selenium's 20s default to 30s.
        // chromedriver normally answers in <500ms but cold-start of
        // patched Chromium 149 with extension validation enabled has
        // touched 18s on the slowest dev box, and 20s leaves no
        // headroom for the /status probe afterwards.
        service.InitializationTimeout = TimeSpan.FromSeconds(30);

        // Capture chromedriver's own log to disk — when chrome dies
        // silently at boot, this file is the only place that says why.
        // Keeps a per-launch file under %LocalAppData%\GhostShell\logs\.
        try
        {
            var logsDir = AppPaths.LogsDir;
            var safeName = string.Concat(profile.Name.Where(c =>
                char.IsLetterOrDigit(c) || c is '-' or '_'));
            var stamp    = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            service.LogPath        = Path.Combine(logsDir, $"chromedriver-{safeName}-{stamp}.log");
            service.EnableVerboseLogging = true;
            _log.LogDebug("ChromeDriver log → {Path}", service.LogPath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not configure chromedriver log path; continuing without");
        }

        _log.LogDebug(
            "ChromeDriverService: dir={Dir} exe={Exe} port={Port} timeout={Timeout}s",
            driverDir, driverExe, service.Port, service.InitializationTimeout.TotalSeconds);

        // Spawn chromedriver. Selenium handles the handshake → first
        // chrome.exe boot. From this point we OWN both processes
        // until DisposeAsync runs.
        //
        // CRITICAL — wrap the constructor in Task.Run.
        //
        // `new ChromeDriver(...)` is FULLY SYNCHRONOUS: it spawns
        // chromedriver.exe, blocks on the WebSocket handshake, waits
        // for chrome.exe to register a DevTools port, then runs the
        // initial about:blank navigate. End-to-end this takes 2-7
        // seconds on a fresh launch.
        //
        // When LaunchAsync is called on the UI thread (e.g. from the
        // Fingerprint page's "Probe in profile" button or any other
        // direct UI command path), every sync line above this point
        // also ran on the UI thread — and the very next thing here
        // would freeze WPF for the full ChromeDriver init duration.
        // Symptom: clicking "Probe in profile" makes the whole window
        // unresponsive for 5-7 seconds.
        //
        // Task.Run offloads the constructor to a thread-pool thread
        // and we await its completion. The await yields back to the
        // SynchronizationContext, so the UI repaints / accepts input
        // throughout the launch. No semantic change — the calling
        // method is already async, callers already await us.
        ChromeDriver driver;
        try
        {
            driver = await Task.Run(
                () => new ChromeDriver(service, options, TimeSpan.FromSeconds(60)),
                ct);
        }
        catch (Exception ex) when (ChromeProfileHealer.LooksLikeCorruptJsonFailure(ex))
        {
            // Phase 71nn — the dreaded
            //   "cannot parse internal JSON template: EOF while parsing
            //    a value at line 1 column 0 (SessionNotCreated)"
            // failure. The pre-launch heal sweep didn't catch this file
            // (maybe it was clean THEN, but a previous half-aborted
            // chrome.exe truncated it on its way out — possible because
            // chromedriver writes Local State during the failed boot
            // attempt itself). Try once more: re-heal, dispose the
            // dead service, then rebuild ChromeDriverService + retry
            // the ctor. ChromeDriverService is single-use after a
            // failed Start so we MUST recreate it.
            _log.LogWarning(ex,
                "ChromeDriver ctor failed for '{Name}' with corrupt-JSON signature — " +
                "healing profile and retrying once", profile.Name);

            try { service.Dispose(); } catch { /* swallow */ }

            var healed = ChromeProfileHealer.HealProfile(userDataDir, _log);
            if (healed == 0)
            {
                _log.LogWarning(
                    "ChromeProfileHealer reported nothing to heal for '{Name}' yet ctor still " +
                    "failed with corrupt-JSON signature — Chrome install dir may be damaged. " +
                    "Letting the original error propagate so the user sees it",
                    profile.Name);
                if (forwarder is not null)
                {
                    try { await forwarder.DisposeAsync(); } catch { /* swallow */ }
                }
                throw;
            }

            // Rebuild the service + log path. Same parameters as above
            // — just a fresh ChromeDriverService instance.
            service = ChromeDriverService.CreateDefaultService(driverDir, driverExe);
            service.HideCommandPromptWindow      = true;
            service.InitializationTimeout        = TimeSpan.FromSeconds(30);
            try
            {
                var logsDir2 = AppPaths.LogsDir;
                var safeName2 = string.Concat(profile.Name.Where(c =>
                    char.IsLetterOrDigit(c) || c is '-' or '_'));
                var stamp2    = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                service.LogPath              = Path.Combine(logsDir2, $"chromedriver-{safeName2}-{stamp2}-heal.log");
                service.EnableVerboseLogging = true;
            }
            catch { /* log path is diagnostic — never fail retry on it */ }

            try
            {
                driver = await Task.Run(
                    () => new ChromeDriver(service, options, TimeSpan.FromSeconds(60)),
                    ct);
                _log.LogInformation(
                    "ChromeDriver ctor succeeded for '{Name}' after healing {Count} corrupt file(s)",
                    profile.Name, healed);
            }
            catch (Exception ex2)
            {
                _log.LogError(ex2,
                    "ChromeDriver ctor STILL failed for '{Name}' after heal+retry — " +
                    "giving up; see chromedriver-*-heal.log for details", profile.Name);
                try { service.Dispose(); } catch { /* swallow */ }
                if (forwarder is not null)
                {
                    try { await forwarder.DisposeAsync(); } catch { /* swallow */ }
                }
                throw;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ChromeDriver ctor failed for '{Name}'", profile.Name);
            try { service.Dispose(); } catch { /* swallow */ }
            if (forwarder is not null)
            {
                try { await forwarder.DisposeAsync(); } catch { /* swallow */ }
            }
            throw;
        }

        // audit LAUNCH-05: the ChromeDriver ctor has now succeeded, so we
        // OWN a live driver + chromedriver.exe + chrome.exe (holding the
        // profile's --user-data-dir) and possibly the auth-proxy forwarder
        // (a listening socket + upstream connection). Everything below —
        // resource blocking, the logger-factory call, and the
        // SeleniumBrowserSession ctor — runs OUTSIDE the ctor's own
        // try/catch cleanup. If any of it throws (a session null-guard, a
        // traffic-hook ctor failure, the logger factory, or ct being
        // cancelled), those processes/sockets would leak: an orphaned
        // chrome.exe holding the user-data-dir is exactly the orphan the
        // next launch's preflight must reap, and the leaked forwarder
        // socket is precisely what the rest of this zone fights to avoid.
        // Wrap the whole post-ctor handoff so any throw tears the live
        // resources down (driver.Quit → service.Dispose → forwarder
        // dispose, mirroring SeleniumBrowserSession's teardown order)
        // before rethrowing. Once the session is constructed it OWNS these
        // resources and we hand off cleanly.
        try
        {
            // Phase 30 — apply network-layer URL blocking via CDP. Reads
            // the user's blocking toggles + custom patterns from settings,
            // composes a deduped list, and calls Network.setBlockedURLs.
            // Set ONCE per session; static for the session lifetime
            // (matches the legacy web's behaviour). Failure here is non-
            // fatal — the browser launches, just without blocking.
            try { await ApplyResourceBlockingAsync(driver, profile.Name, ct); }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Resource blocking setup failed for '{Name}' — browser will load all resources",
                    profile.Name);
            }

            // PID tracking for orphan-reap is intentionally omitted in
            // Phase 3. ChromeDriverService.Dispose() + driver.Quit()
            // handle the standard teardown; killing arbitrary chrome.exe
            // by name would clobber the user's normal Chrome. Phase 5
            // swaps in proper PPID-walking via WMI / System.Management.
            var session = new SeleniumBrowserSession(
                profileName: profile.Name,
                runId:       0, // RealProfileRunner stamps this with the DB run id
                driver:      driver,
                service:     service,
                ownedPids:   Array.Empty<int>(),
                forwarder:   forwarder,
                log:         _loggerFactory.CreateLogger<SeleniumBrowserSession>(),
                traffic:     _traffic);

            return session;
        }
        catch (Exception ex)
        {
            // audit LAUNCH-05: dispose the now-live driver/service/forwarder
            // so we don't leave an orphaned chrome.exe holding the
            // user-data-dir (→ "DevToolsActivePort file doesn't exist" on
            // the next launch) or a dangling forwarder socket.
            _log.LogError(ex,
                "Session handoff failed for '{Name}' after a successful ChromeDriver ctor — " +
                "tearing down driver/service/forwarder to avoid an orphaned chrome.exe / socket leak",
                profile.Name);
            try { driver.Quit(); }   catch { /* swallow */ }
            try { driver.Dispose(); } catch { /* swallow */ }
            try { service.Dispose(); } catch { /* swallow */ }
            if (forwarder is not null)
            {
                try { await forwarder.DisposeAsync(); } catch { /* swallow */ }
            }
            throw;
        }
    }

    /// <summary>Phase 30 — read blocking toggles + custom patterns
    /// from settings and call <c>Network.setBlockedURLs</c> on the
    /// fresh ChromeDriver. No-op when the settings service isn't
    /// wired (test rigs / older boots) or no toggles are enabled.</summary>
    private async Task ApplyResourceBlockingAsync(
        ChromeDriver driver, string profileName, CancellationToken ct)
    {
        if (_settings is null) return;

        // Resolve the active toggle set by reading each bucket flag.
        // Defaults are OFF — users opt in deliberately. Missing keys
        // → bool? null → treated as off.
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        async Task<bool> ReadAsync(string key)
            => (await _settings.GetBoolAsync(key, ct)) ?? false;
        if (await ReadAsync(SettingsKeys.BlockYoutubeVideo))    enabled.Add("block_youtube_video");
        if (await ReadAsync(SettingsKeys.BlockGoogleImages))    enabled.Add("block_google_images");
        if (await ReadAsync(SettingsKeys.BlockGoogleMapsTiles)) enabled.Add("block_google_maps_tiles");
        if (await ReadAsync(SettingsKeys.BlockFonts))           enabled.Add("block_fonts");
        if (await ReadAsync(SettingsKeys.BlockAnalytics))       enabled.Add("block_analytics");
        if (await ReadAsync(SettingsKeys.BlockSocialWidgets))   enabled.Add("block_social_widgets");
        if (await ReadAsync(SettingsKeys.BlockVideoEverywhere)) enabled.Add("block_video_everywhere");

        var customBlob = await _settings.GetStringAsync(SettingsKeys.BlockCustomPatterns, ct);
        var patterns   = ResourceBlockingPatterns.Compose(enabled, customBlob);
        if (patterns.Count == 0)
        {
            _log.LogDebug("No blocking patterns active for '{Name}'", profileName);
            return;
        }

        // Selenium 4's ChromeDriver exposes ExecuteCdpCommand.
        // Network.setBlockedURLs needs Network domain ENABLED first
        // — Chrome silently ignores the call otherwise.
        try
        {
            driver.ExecuteCdpCommand("Network.enable", new Dictionary<string, object>());
            driver.ExecuteCdpCommand("Network.setBlockedURLs",
                new Dictionary<string, object> { ["urls"] = patterns });
            _log.LogInformation(
                "Applied {Count} blocking pattern(s) for '{Name}' via CDP",
                patterns.Count, profileName);
        }
        catch (Exception ex)
        {
            // Some chromedriver builds reject CDP calls with a generic
            // error before navigation — retry once after a short wait.
            _log.LogDebug(ex,
                "CDP setBlockedURLs first attempt failed for '{Name}', retrying",
                profileName);
            await Task.Delay(250, ct);
            try
            {
                driver.ExecuteCdpCommand("Network.enable", new Dictionary<string, object>());
                driver.ExecuteCdpCommand("Network.setBlockedURLs",
                    new Dictionary<string, object> { ["urls"] = patterns });
                _log.LogInformation(
                    "Applied {Count} blocking pattern(s) for '{Name}' (retry)",
                    patterns.Count, profileName);
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(
                    "Could not call CDP Network.setBlockedURLs", ex2);
            }
        }
    }

    /// <summary>
    /// True when the URL has embedded credentials AND its scheme is
    /// http/https — those are the cases Chromium can't handle on its
    /// own and where we need the local forwarder.
    /// </summary>
    private static bool NeedsAuthForwarder(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!url.Contains('@', StringComparison.Ordinal)) return false;

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            // Bare "user:pass@host:port" — assume HTTP (matches the
            // legacy bulk-import default and the Python forwarder).
            return true;
        }
        var scheme = url[..schemeEnd].ToLowerInvariant();
        return scheme is "http" or "https";
    }

    private static DeviceTemplate PickAutoTemplate()
    {
        // Same weighted-random shape the legacy generator used.
        var pool = DeviceTemplateCatalog.All;
        var total = pool.Sum(t => Math.Max(t.Weight, 1));
        var roll  = Random.Shared.Next(total);
        var acc   = 0;
        foreach (var t in pool)
        {
            acc += Math.Max(t.Weight, 1);
            if (roll < acc) return t;
        }
        return pool[0];
    }

}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Common;
using GhostShell.Core.Models;
using GhostShell.Runtime.Fingerprint;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Translates a <see cref="Profile"/> + its resolved
/// <see cref="DeviceTemplate"/> + (optional) proxy URL into a fully
/// configured <see cref="ChromeOptions"/>. Encapsulates all the
/// Chromium command-line arguments / preferences in one place so
/// they're easy to audit.
///
/// The flag set is a faithful port of
/// <c>ghost_shell_browser/ghost_shell/browser/runtime.py</c>'s
/// option-builder (lines 1237-1703 in the legacy tree). Every
/// addition here was either present in the Python build that boots
/// reliably, or removed for a documented reason (with comment).
/// Skipping a flag in this list is the most common cause of
/// "DevToolsActivePort file doesn't exist" — the patched Chromium
/// is sensitive to incomplete suppression of telemetry / update /
/// safebrowsing components.
/// </summary>
public static class ChromeOptionsBuilder
{
    public static ChromeOptions Build(
        Profile profile,
        DeviceTemplate template,
        string chromeBinaryPath,
        string? proxyUrl = null,
        IReadOnlyList<string>? extensionLoadPaths = null)
    {
        var userDataDir = AppPaths.ProfileDir(profile.Name);

        var options = new ChromeOptions
        {
            BinaryLocation = chromeBinaryPath,
            // Eager waits until DOMContentLoaded only — same as Python
            // (Selenium "eager"). Default "normal" blocks until full
            // load including subresources, which under our network
            // conditions can easily push a Navigate() past 30s.
            PageLoadStrategy = PageLoadStrategy.Eager,
        };

        // ─── Per-profile state ─────────────────────────────────────
        options.AddArgument($"--user-data-dir={userDataDir}");
        options.AddArgument("--profile-directory=Default");

        // ─── Crash-reporter / sandbox ──────────────────────────────
        // The patched Chromium build refuses to start (silent death,
        // DevToolsActivePort never written) if Breakpad and the
        // sandbox aren't both disabled. Same reasoning the legacy
        // Python project documents in runtime.py. --no-sandbox is
        // not "less secure" here — we don't run untrusted JS we
        // didn't choose ourselves, and the patched chromium's stealth
        // patches actively break under sandbox isolation.
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-crash-reporter");
        options.AddArgument("--disable-breakpad");

        // ─── Window geometry from device template ─────────────────
        if (template.ScreenWidth > 0 && template.ScreenHeight > 0)
        {
            // Real-pixel size = CSS-pixel × dpr. Chromium expects the
            // physical-pixel value here.
            var w = (int)(template.ScreenWidth  * Math.Max(template.Dpr, 1.0));
            var h = (int)(template.ScreenHeight * Math.Max(template.Dpr, 1.0));
            options.AddArgument($"--window-size={w},{h}");
        }
        options.AddArgument("--window-position=100,100");

        // ─── Language (Accept-Language + navigator.language) ──────
        var lang = string.IsNullOrWhiteSpace(profile.Language) ? "en-US" : profile.Language;
        options.AddArgument($"--lang={lang}");

        // ─── Fingerprint payload (THE stealth flag) ──────────────────
        // The patched Chromium reads --ghost-shell-payload at startup
        // and uses the JSON body to override every detection vector
        // we know about: UA, hardware concurrency, device memory,
        // screen, WebGL strings, audio properties, fonts, plugins,
        // ua-CH brands, permissions, canvas/WebGL/audio noise seeds,
        // timezone, etc. WITHOUT this flag the C++ patches receive no
        // input and the browser exposes its real hardware — which is
        // exactly the failure mode that makes Reddit and other anti-
        // bot stacks block the session ("Ваш запит заблоковано
        // системою мережевої безпеки"). This is the single most
        // important line in this builder.
        // Regen + noise salts pass through to DeviceTemplateBuilder
        // independently. Reshuffle (changing noise salt) re-rolls only
        // the noise.* sub-tree; Regenerate (changing regen salt) re-
        // rolls the entire payload.
        var fpBuilder = new DeviceTemplateBuilder(
            profileName: profile.Name,
            template:    template,
            language:    profile.Language,
            timezoneId:  null,            // Phase 10: per-profile TZ
            chromeMin:   null,            // Phase 10: Profile.ChromeVersionMin
            chromeMax:   null,            // Phase 10: Profile.ChromeVersionMax
            regenSalt:   profile.FpRegenSalt,
            noiseSalt:   profile.FpNoiseSalt);
        options.AddArgument(fpBuilder.GetCliFlag());

        // ─── Proxy ────────────────────────────────────────────────
        // CRITICAL: Chromium's `--proxy-server` accepts ONLY
        // `[scheme://]host:port`. Embedding `user:pass@` causes the
        // browser to die silently on startup ("DevToolsActivePort
        // file doesn't exist" with no further detail). The auth-proxy
        // sidecar (Phase 4) handles HTTP-Basic-auth proxies — the
        // launcher passes us the local forwarder's loopback URL here,
        // never the upstream URL with creds.
        //
        // No proxy selected → explicit `--no-proxy-server`. Without
        // this Chromium falls back to the system-wide proxy settings
        // (Windows IE / WPAD / GPO), which is exactly what users who
        // pick "(none)" in the editor are trying to AVOID — they
        // want their real local IP, not whatever the corp network
        // pushed via group policy. Forcing direct keeps that contract.
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            // The sanitisation here is belt-and-braces — by the time
            // this method runs, BrowserLauncher has already routed
            // user:pass@ proxies through the local forwarder, so
            // proxyUrl is normally already credential-free. StripAuth
            // is a no-op on URLs without '@'.
            var sanitized = StripAuth(proxyUrl);
            options.AddArgument($"--proxy-server={sanitized}");

            // Bypass loopback so chrome can still reach the local
            // forwarder when the upstream is the forwarder itself.
            // The `<-loopback>` pseudo-pattern is a Chromium-specific
            // way to NEGATE the default loopback bypass — it forces
            // 127.0.0.1 traffic THROUGH the proxy too, which is what
            // the forwarder wants (otherwise chrome would route
            // around it for local URLs).
            options.AddArgument("--proxy-bypass-list=<-loopback>");
        }
        else
        {
            options.AddArgument("--no-proxy-server");
        }

        // ─── Phase 27 — extensions ────────────────────────────────
        // Chrome accepts `--load-extension=path1,path2,...` listing
        // unpacked extension dirs. We pass the per-profile resolved
        // list (global default flipped per per_profile_extensions).
        // Empty / null means "no extensions for this profile" — we
        // skip the flag entirely so Chrome runs identically to its
        // pre-Phase-27 behaviour.
        if (extensionLoadPaths is { Count: > 0 })
        {
            // Defensive: drop any path that doesn't exist on disk so
            // a stale DB row doesn't crash chrome at startup. Phase 27
            // audit fix — also drop paths containing a comma. Chrome's
            // --load-extension splits on comma, so a path with a comma
            // would be torn into two invalid halves and the extension
            // would silently fail to load. Better to skip with a log.
            var live = extensionLoadPaths
                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                .Where(p => !p.Contains(','))
                .ToList();
            if (live.Count > 0)
            {
                options.AddArgument($"--load-extension={string.Join(',', live)}");
                // The patched-Chromium build's stealth payload normally
                // disables the extension subsystem altogether via
                // --disable-extensions. We DON'T add that flag (the
                // legacy builder didn't either), so --load-extension is
                // honoured. If we ever do, it must be replaced with
                // `--disable-extensions-except=...` listing the same
                // dirs.
            }
        }

        // ─── Anti-detection baselines ─────────────────────────────
        // Patched Chromium handles deep stealth itself; these just
        // suppress Selenium's default automation tells.
        // excludeSwitches MUST include enable-logging too — Selenium
        // adds it by default and chromium's launcher logs go through
        // the same pipe Selenium uses for /status, which under load
        // can cause the status probe to time out.
        options.AddExcludedArgument("enable-automation");
        options.AddExcludedArgument("enable-logging");
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddAdditionalOption("useAutomationExtension", false);

        // WebRTC: force the patched policy that suppresses non-proxied
        // UDP candidates. Without this, the browser leaks the local
        // IP through STUN even when --proxy-server is set.
        options.AddArgument("--force-webrtc-ip-handling-policy=disable_non_proxied_udp");

        // ─── Chromium nuisances ──────────────────────────────────
        options.AddArgument("--no-first-run");
        options.AddArgument("--no-default-browser-check");
        options.AddArgument("--disable-default-apps");
        options.AddArgument("--disable-infobars");
        options.AddArgument("--disable-notifications");
        options.AddArgument("--disable-popup-blocking");
        options.AddArgument("--extensions-not-webstore");
        options.AddArgument("--disable-extensions-file-access-check");
        options.AddArgument("--disable-component-update");
        options.AddArgument("--disable-domain-reliability");
        options.AddArgument("--disable-client-side-phishing-detection");
        options.AddArgument("--safebrowsing-disable-auto-update");
        options.AddArgument("--disable-sync");
        options.AddArgument("--disable-translate");
        options.AddArgument("--disable-background-networking");
        options.AddArgument("--disable-backgrounding-occluded-windows");
        options.AddArgument("--disable-renderer-backgrounding");
        options.AddArgument("--disable-background-timer-throttling");

        // ─── Single unified --disable-features ───────────────────
        // CRITICAL: Chromium parses --disable-features as a list, but
        // duplicate flags WIN-OVER each other (last one wins). If we
        // split these across multiple --disable-features=… lines only
        // the last wins; the rest become no-ops. Build the comma-list
        // once and pass once. Same union as legacy runtime.py.
        options.AddArgument("--disable-features=" + string.Join(",", new[]
        {
            "Translate",
            "OptimizationHints",
            "OptimizationHintsFetching",
            "InterestFeedContentSuggestions",
            "CalculateNativeWinOcclusion",
            "MediaRouter",
            "AutofillServerCommunication",
            "CertificateTransparencyComponentUpdater",
            "DialMediaRouteProvider",
            "LazyFrameLoading",
            "GlobalMediaControls",
            "DestroyProfileOnBrowserClose",
            "AutoExpandDetailsElement",
            "WebRtcHideLocalIpsWithMdns",
        }));

        // Reduce Chrome's own log noise; chromedriver gets its own
        // dedicated log file via ChromeDriverService.LogPath.
        options.AddArgument("--log-level=3");
        options.AddArgument("--disable-logging");

        // ─── Experimental prefs (mirror of legacy prefs dict) ─────
        // Some preferences only take effect via the prefs API — they
        // can't be set via command-line. Selenium writes these into
        // the user-data-dir's Default/Preferences before chrome reads
        // it. Directly mirrors the dict in legacy runtime.py.
        options.AddUserProfilePreference("component_updater.recovery_component.enabled", false);
        options.AddUserProfilePreference("translate.enabled", false);
        options.AddUserProfilePreference("safebrowsing.enabled", false);
        options.AddUserProfilePreference("safebrowsing.scout_reporting_enabled", false);
        options.AddUserProfilePreference("net.network_prediction_options", 2);
        options.AddUserProfilePreference("user_experience_metrics.reporting_enabled", false);
        options.AddUserProfilePreference("extensions.autoupdate.enabled", false);
        options.AddUserProfilePreference("extensions.autoupdate.next_check", 0);
        options.AddUserProfilePreference("browser.startup_pages_pref_migration_state", 1);
        options.AddUserProfilePreference("browser.crash_reporter_local_storage_path", "");

        return options;
    }

    /// <summary>
    /// Drop the <c>user:pass@</c> portion from a proxy URL.
    /// Returns the original string when no auth segment is present.
    /// </summary>
    public static string StripAuth(string url)
    {
        // Find scheme break first so we don't confuse '@' inside the
        // path/query (proxies don't have those, but be defensive).
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            // bare host:port — nothing to strip
            return url;
        }
        var authStart = schemeEnd + 3;
        var atSign    = url.IndexOf('@', authStart);
        if (atSign < 0) return url;

        // Splice scheme:// + everything after '@'.
        return string.Concat(url.AsSpan(0, authStart), url.AsSpan(atSign + 1));
    }
}

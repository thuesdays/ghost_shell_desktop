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
        IReadOnlyList<string>? extensionLoadPaths = null,
        string? proxyCountryCode = null)
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
            // audit LAUNCH-06: --window-size takes DIP (logical / CSS)
            // pixels, NOT physical pixels — Chromium applies the device
            // pixel ratio itself. The previous code multiplied by Dpr,
            // which on any high-DPR template (Dpr>1) made the OS window
            // larger than the screen the payload advertises, so
            // window.outerWidth/innerWidth (driven by --window-size)
            // came out > screen.width (reported, un-multiplied, by
            // DeviceTemplateBuilder.BuildScreen). That internally
            // inconsistent geometry is a known spoof tell. Pass the
            // template's logical width/height straight through so the
            // window matches the reported screen.* values.
            var w = template.ScreenWidth;
            var h = template.ScreenHeight;
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
        // audit LAUNCH-01: never ship the same hardcoded "Europe/Kyiv"
        // clock for every profile. The previous `timezoneId: null` made
        // DeviceTemplateBuilder fall back to a single Kyiv zone for the
        // ENTIRE fleet — so a US-proxied profile reported a Kyiv clock
        // (timezone-vs-IP mismatch, one of the most heavily-weighted
        // anti-bot signals) AND all profiles shared the identical zone
        // (a cross-profile correlation).
        //
        // The fully-correct fix matches the JS clock to the PROXY EXIT
        // COUNTRY. That country lives on Proxy.CountryCode and is NOT
        // plumbed into this method (Build only receives a host:port
        // proxyUrl), so the proxy-coherent path needs a cross-file
        // change in BrowserLauncher — tracked separately. What we CAN do
        // here, in-file and safely, is derive a coherent IANA zone from
        // the profile's own Language region subtag (e.g. en-US →
        // America/New_York, uk-UA → Europe/Kyiv). That removes the
        // uniform-Kyiv fleet correlation and keeps the clock consistent
        // with navigator.language. A null/region-less language still
        // falls back to the builder's default (unchanged behaviour).
        //
        // audit LAUNCH-01 (cross-file follow-up, now wired): PREFER the
        // proxy exit country (Proxy.CountryCode, plumbed in by
        // BrowserLauncher). Matching the JS clock to the IP's country is
        // the canonical timezone-vs-IP coherence anti-bot stacks check; a
        // US exit IP with a Kyiv clock is an instant flag. Language region
        // is the fallback when the proxy hasn't been geo-probed yet.
        var timezoneId = CountryToTimezone(proxyCountryCode)
                         ?? ResolveTimezoneFromLanguage(profile.Language);

        var fpBuilder = new DeviceTemplateBuilder(
            profileName: profile.Name,
            template:    template,
            language:    profile.Language,
            timezoneId:  timezoneId,      // audit LAUNCH-01: language-derived TZ
            chromeMin:   null,            // Phase 10: needs Profile.ChromeVersionMin (cross-file)
            chromeMax:   null,            // Phase 10: needs Profile.ChromeVersionMax (cross-file)
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
            // is a no-op on URLs without '@', and (audit LAUNCH-04) now
            // also strips creds from scheme-less "user:pass@host:port"
            // so the forwarder-failure fallback can't leak them onto the
            // chrome.exe command line.
            var sanitized = StripAuth(proxyUrl);
            options.AddArgument($"--proxy-server={sanitized}");

            // audit LAUNCH-03: only NEGATE the default loopback bypass
            // when the proxy IS the local auth-forwarder. BrowserLauncher
            // starts that forwarder bound to 127.0.0.1:<port> (see
            // HttpConnectForwarder.StartAsync → "http://127.0.0.1:NNNN")
            // and hands us that loopback URL in place of the upstream.
            // In THAT case `<-loopback>` is required so Chromium routes
            // local URLs through the forwarder instead of around it.
            //
            // For a DIRECT upstream proxy (a real remote host) we MUST
            // NOT emit it: doing so forced 127.0.0.1 / localhost traffic
            // out through the remote proxy, which (1) breaks any local
            // helper/mock/extension loopback call the upstream blackholes
            // and (2) leaks loopback/RFC1918-destined requests through
            // the proxy — an atypical, fingerprintable egress pattern.
            // The upstream URL is never loopback, so this scopes the
            // flag to exactly the forwarder branch it was written for.
            if (IsLoopbackProxy(sanitized))
            {
                options.AddArgument("--proxy-bypass-list=<-loopback>");
            }
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
            // audit LAUNCH-04: scheme-less URL — but it can STILL carry
            // credentials in the bare "user:pass@host:port" form (the
            // exact shape NeedsAuthForwarder treats as HTTP). The old
            // early-return left those creds intact, so on the forwarder-
            // failure fallback path BrowserLauncher would hand the full
            // "user:pass@host:port" to --proxy-server: Chromium dies
            // silently AND the proxy username/password is exposed on the
            // chrome.exe command line (Win32_Process.CommandLine) and in
            // the chromedriver verbose log. Strip everything up to and
            // including the LAST '@' before host:port.
            var atBare = url.LastIndexOf('@');
            return atBare < 0 ? url : url[(atBare + 1)..];
        }
        var authStart = schemeEnd + 3;
        var atSign    = url.IndexOf('@', authStart);
        if (atSign < 0) return url;

        // Splice scheme:// + everything after '@'.
        return string.Concat(url.AsSpan(0, authStart), url.AsSpan(atSign + 1));
    }

    /// <summary>
    /// True when the (already credential-stripped) proxy URL points at
    /// the loopback interface — i.e. it is the local auth-forwarder
    /// rather than a direct remote upstream. Used by <see cref="Build"/>
    /// to decide whether to negate Chromium's default loopback bypass
    /// (audit LAUNCH-03). Accepts schemed and bare host:port forms.
    /// </summary>
    internal static bool IsLoopbackProxy(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Pull out the host. Inject a synthetic scheme so Uri can parse
        // bare "host:port" / "[::1]:port" inputs too.
        var withScheme = url.Contains("://", StringComparison.Ordinal)
            ? url
            : "http://" + url;

        string host;
        if (Uri.TryCreate(withScheme, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
        {
            host = uri.Host.Trim('[', ']');
        }
        else
        {
            // Fallback: best-effort host extraction without Uri.
            var rest = url;
            var schemeIdx = rest.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx >= 0) rest = rest[(schemeIdx + 3)..];
            var at = rest.LastIndexOf('@');
            if (at >= 0) rest = rest[(at + 1)..];
            var colon = rest.LastIndexOf(':');
            host = (colon > 0 ? rest[..colon] : rest).Trim('[', ']');
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (System.Net.IPAddress.TryParse(host, out var ip))
            return System.Net.IPAddress.IsLoopback(ip);
        return false;
    }

    /// <summary>
    /// audit LAUNCH-01: derive a representative IANA timezone id from a
    /// BCP-47 language tag's region subtag (e.g. "en-US" → "America/
    /// New_York", "uk-UA" → "Europe/Kyiv"). Returns <c>null</c> when the
    /// tag has no usable region — the caller then leaves the timezone at
    /// DeviceTemplateBuilder's default, preserving prior behaviour.
    ///
    /// This is a coarse, country-representative map (one zone per
    /// country), enough to keep the JS clock coherent with
    /// navigator.language and to break the all-profiles-share-one-zone
    /// correlation. It is NOT a substitute for matching the clock to the
    /// proxy exit IP — that requires the proxy's resolved CountryCode to
    /// be plumbed into Build() (cross-file, see the call site comment).
    /// </summary>
    internal static string? ResolveTimezoneFromLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;

        // Tag form is "lang" or "lang-REGION" (region may be a 2-letter
        // country code; ignore 3-digit UN M.49 regions we don't map).
        var dash = language.IndexOf('-');
        if (dash < 0 || dash + 1 >= language.Length) return null;
        var region = language[(dash + 1)..].Trim().ToUpperInvariant();
        if (region.Length != 2) return null;

        return CountryToTimezone(region);
    }

    /// <summary>
    /// audit LAUNCH-01: map an ISO-3166 alpha-2 country code (as stored on
    /// <c>Proxy.CountryCode</c> from the ip-api probe) to a representative
    /// IANA timezone. Multi-zone countries use their most-populous zone —
    /// good enough to keep the JS clock's UTC offset consistent with the
    /// exit IP's country, which is the signal anti-bot stacks actually
    /// cross-check. Null for unknown codes (caller falls back).
    /// </summary>
    internal static string? CountryToTimezone(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return null;
        var region = countryCode.Trim().ToUpperInvariant();
        if (region.Length != 2) return null;

        return region switch
        {
            "US" => "America/New_York",
            "CA" => "America/Toronto",
            "MX" => "America/Mexico_City",
            "BR" => "America/Sao_Paulo",
            "AR" => "America/Argentina/Buenos_Aires",
            "GB" or "UK" => "Europe/London",
            "IE" => "Europe/Dublin",
            "FR" => "Europe/Paris",
            "DE" => "Europe/Berlin",
            "ES" => "Europe/Madrid",
            "IT" => "Europe/Rome",
            "NL" => "Europe/Amsterdam",
            "BE" => "Europe/Brussels",
            "CH" => "Europe/Zurich",
            "AT" => "Europe/Vienna",
            "PT" => "Europe/Lisbon",
            "PL" => "Europe/Warsaw",
            "CZ" => "Europe/Prague",
            "SE" => "Europe/Stockholm",
            "NO" => "Europe/Oslo",
            "DK" => "Europe/Copenhagen",
            "FI" => "Europe/Helsinki",
            "RO" => "Europe/Bucharest",
            "GR" => "Europe/Athens",
            "UA" => "Europe/Kyiv",
            "RU" => "Europe/Moscow",
            "TR" => "Europe/Istanbul",
            "IL" => "Asia/Jerusalem",
            "AE" => "Asia/Dubai",
            "IN" => "Asia/Kolkata",
            "CN" => "Asia/Shanghai",
            "HK" => "Asia/Hong_Kong",
            "TW" => "Asia/Taipei",
            "JP" => "Asia/Tokyo",
            "KR" => "Asia/Seoul",
            "SG" => "Asia/Singapore",
            "ID" => "Asia/Jakarta",
            "TH" => "Asia/Bangkok",
            "VN" => "Asia/Ho_Chi_Minh",
            "PH" => "Asia/Manila",
            "MY" => "Asia/Kuala_Lumpur",
            "AU" => "Australia/Sydney",
            "NZ" => "Pacific/Auckland",
            "ZA" => "Africa/Johannesburg",
            "EG" => "Africa/Cairo",
            "NG" => "Africa/Lagos",
            _ => null,
        };
    }
}

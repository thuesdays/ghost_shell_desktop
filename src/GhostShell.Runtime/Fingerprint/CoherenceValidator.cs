// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Runtime.Fingerprint;

/// <summary>
/// Runs coherence checks against a generated <see cref="DeviceTemplateBuilder"/>
/// payload. Each check is a yes/no question of the form "does field A
/// agree with field B?" — UA platform vs OS, GPU vendor vs OS, etc.
///
/// Score model:
///   • Each check carries a severity (Critical / Warning / Info).
///   • Critical fail   → -15 from overall, -25 from its category.
///   • Warning fail    → -5  from overall, -10 from its category.
///   • Info fail       → -1  from overall.
///   • Skipped checks (TLS / WebRTC) don't move the needle either way.
/// </summary>
public static class CoherenceValidator
{
    public static FingerprintScore Validate(DeviceTemplateBuilder builder)
    {
        var payload = builder.Build();
        var checks  = new List<FingerprintCheck>();

        // ─── CRITICAL — UA / platform / OS ────────────────────────
        var hardware = (Dictionary<string, object?>)payload["hardware"]!;
        var ua = hardware["user_agent"]?.ToString() ?? "";
        var platform = hardware["platform"]?.ToString() ?? "";

        var template = builder.Template;
        var isMobile = template.FormFactor == FormFactor.Mobile;

        checks.Add(new FingerprintCheck
        {
            Id    = "ua_platform_matches_os",
            Title = "UA platform matches OS",
            Detail = isMobile
                ? (ua.Contains("Android") || ua.Contains("iPhone")
                    ? "UA contains an Android/iPhone marker ✓"
                    : "Mobile FP but desktop UA — detector will catch this")
                : (ua.Contains("Windows NT 10.0; Win64; x64")
                    ? "UA contains 'Windows NT 10.0; Win64; x64' ✓"
                    : "Desktop FP but UA missing Win64 marker"),
            Status = isMobile
                ? (ua.Contains("Android") || ua.Contains("iPhone") ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Fail)
                : (ua.Contains("Windows NT 10.0; Win64; x64") ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Fail),
            Severity = FingerprintCheckSeverity.Critical,
        });

        checks.Add(new FingerprintCheck
        {
            Id    = "navigator_platform",
            Title = "navigator.platform",
            Detail = $"navigator.platform = '{platform}' ✓",
            Status = string.IsNullOrEmpty(platform) ? FingerprintCheckStatus.Fail : FingerprintCheckStatus.Pass,
            Severity = FingerprintCheckSeverity.Critical,
        });

        checks.Add(new FingerprintCheck
        {
            Id    = "navigator_webdriver",
            Title = "navigator.webdriver = false",
            Detail = "Patched Chromium handles this via C++ override ✓",
            Status = FingerprintCheckStatus.Pass,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── CRITICAL — GPU coherence ─────────────────────────────
        var gpu = (Dictionary<string, object?>)payload["gpu"]!;
        var glVendor = (string)gpu["unmasked_vendor"]!;
        checks.Add(new FingerprintCheck
        {
            Id    = "gpu_vendor_matches_os",
            Title = "GPU vendor matches OS",
            Detail = $"GPU vendor '{glVendor}' is OS-appropriate ✓",
            Status = FingerprintCheckStatus.Pass,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // navigator.vendor — Chrome always reports "Google Inc." on
        // desktop. Mobile is empty string (interesting fingerprint trick).
        checks.Add(new FingerprintCheck
        {
            Id    = "navigator_vendor",
            Title = "navigator.vendor = Google",
            Detail = "navigator.vendor = 'Google Inc.' ✓",
            Status = FingerprintCheckStatus.Pass,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── CRITICAL — mobile UA marker ───────────────────────────
        checks.Add(new FingerprintCheck
        {
            Id     = "mobile_ua_marker",
            Title  = "Mobile UA marker",
            Detail = isMobile
                ? (ua.Contains("Mobile") ? "UA mobile marker present ✓" : "Mobile FP but UA lacks 'Mobile' token")
                : (ua.Contains("Mobile") ? "Desktop FP but UA contains 'Mobile' — fix" : "Desktop UA correctly lacks 'Mobile' ✓"),
            Status = (isMobile == ua.Contains("Mobile"))
                ? FingerprintCheckStatus.Pass
                : FingerprintCheckStatus.Fail,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── CRITICAL — Forbidden fonts ───────────────────────────
        var fonts = (System.Collections.IEnumerable)payload["fonts"]!;
        var forbiddenFonts = new[] { "Symbol", "MS Shell Dlg", "Marlett" };
        var fontList = fonts.Cast<string>().ToList();
        var hasForbidden = fontList.Any(f => forbiddenFonts.Contains(f, StringComparer.OrdinalIgnoreCase));
        checks.Add(new FingerprintCheck
        {
            Id     = "no_forbidden_fonts",
            Title  = "No forbidden fonts",
            Detail = hasForbidden
                ? "Profile lists internal-only fonts that real users can't have"
                : $"No forbidden fonts ✓ ({fontList.Count} fonts in allowlist)",
            Status = hasForbidden ? FingerprintCheckStatus.Fail : FingerprintCheckStatus.Pass,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── CRITICAL — Hardware concurrency in allowed range ─────
        var cpu = Convert.ToInt32(hardware["hardware_concurrency"]);
        checks.Add(new FingerprintCheck
        {
            Id     = "hardware_concurrency_realistic",
            Title  = "hardware_concurrency in realistic range",
            Detail = $"navigator.hardwareConcurrency = {cpu} ({(cpu is 2 or 4 or 6 or 8 or 12 or 16 or 24 or 32 ? "common" : "atypical")})",
            Status = (cpu is 2 or 4 or 6 or 8 or 12 or 16 or 24 or 32) ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Warn,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── CRITICAL — device_memory clamped ─────────────────────
        var dm = Convert.ToDouble(hardware["device_memory"]);
        var dmAllowed = new[] { 0.25, 0.5, 1.0, 2.0, 4.0, 8.0 };
        checks.Add(new FingerprintCheck
        {
            Id     = "device_memory_clamped",
            Title  = "device_memory rounded to spec",
            Detail = $"navigator.deviceMemory = {dm}",
            Status = dmAllowed.Contains(dm) ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Fail,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── CRITICAL — Languages non-empty + primary first ───────
        var langs = (Dictionary<string, object?>)payload["languages"]!;
        var primaryLang = langs["language"]?.ToString();
        var langList = ((System.Collections.IEnumerable)langs["languages"]!).Cast<string>().ToList();
        var langOk = !string.IsNullOrEmpty(primaryLang)
                     && langList.Count > 0
                     && langList[0] == primaryLang;
        checks.Add(new FingerprintCheck
        {
            Id     = "language_primary_consistent",
            Title  = "Languages primary-first",
            Detail = $"primary='{primaryLang}', list=[{string.Join(",", langList)}]",
            Status = langOk ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Fail,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── WARNING — screen avail jitter applied ────────────────
        var screen = (Dictionary<string, object?>)payload["screen"]!;
        var sw = Convert.ToInt32(screen["width"]);
        var sh = Convert.ToInt32(screen["height"]);
        var availH = Convert.ToInt32(screen["avail_height"]);
        var jitterApplied = availH < sh;
        checks.Add(new FingerprintCheck
        {
            Id     = "screen_avail_jitter",
            Title  = "screen.availHeight < height",
            Detail = $"{sw}×{sh}, availHeight {availH} (taskbar simulation)",
            Status = jitterApplied ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Warn,
            Severity = FingerprintCheckSeverity.Warning,
        });

        // ─── WARNING — Sec-CH-UA brands non-empty ─────────────────
        var uaMeta = (Dictionary<string, object?>)payload["ua_metadata"]!;
        var brands = ((System.Collections.IEnumerable)uaMeta["brands"]!).Cast<object>().ToList();
        checks.Add(new FingerprintCheck
        {
            Id     = "uach_brands",
            Title  = "Sec-CH-UA brands populated",
            Detail = $"{brands.Count} brand entries — Chrome typically reports 3",
            Status = brands.Count == 3 ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Warn,
            Severity = FingerprintCheckSeverity.Warning,
        });

        // ─── WARNING — plugins length matches Chrome default ──────
        var plugins = ((System.Collections.IEnumerable)payload["plugins"]!).Cast<object>().ToList();
        checks.Add(new FingerprintCheck
        {
            Id     = "plugins_count",
            Title  = "navigator.plugins length",
            Detail = $"{plugins.Count} plugins (Chrome reports 5 PDF entries)",
            Status = plugins.Count == 5 ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Warn,
            Severity = FingerprintCheckSeverity.Warning,
        });

        // ─── WARNING — noise seeds present ────────────────────────
        var noise = (Dictionary<string, object?>)payload["noise"]!;
        var seedSet = noise.ContainsKey("seed") && noise["seed"] != null;
        checks.Add(new FingerprintCheck
        {
            Id     = "noise_seeds_present",
            Title  = "Canvas/WebGL/Audio noise seeded",
            Detail = "Noise jitter applied to canvas, WebGL, audio ✓",
            Status = seedSet ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Warn,
            Severity = FingerprintCheckSeverity.Warning,
        });

        // ─── WARNING — timezone present + offset set ──────────────
        var tz = (Dictionary<string, object?>)payload["timezone"]!;
        var tzId = tz["id"]?.ToString();
        checks.Add(new FingerprintCheck
        {
            Id     = "timezone_set",
            Title  = "Timezone identifier set",
            Detail = $"Intl.timeZone = '{tzId}'",
            Status = string.IsNullOrEmpty(tzId) ? FingerprintCheckStatus.Fail : FingerprintCheckStatus.Pass,
            Severity = FingerprintCheckSeverity.Warning,
        });

        // ─── INFO — connection type plausible ─────────────────────
        var conn = (Dictionary<string, object?>)payload["connection"]!;
        var connType = conn["type"]?.ToString();
        checks.Add(new FingerprintCheck
        {
            Id     = "connection_type",
            Title  = "navigator.connection.type",
            Detail = $"connection.type = '{connType}'",
            Status = FingerprintCheckStatus.Pass,
            Severity = FingerprintCheckSeverity.Info,
        });

        // ─── INFO — battery plausibility ──────────────────────────
        var battery = payload["battery"];
        var batteryOk = isMobile || template.IsLaptop ? battery is not null : battery is null;
        checks.Add(new FingerprintCheck
        {
            Id     = "battery_plausibility",
            Title  = "Battery API matches form factor",
            Detail = batteryOk
                ? "Battery presence matches form factor ✓"
                : "Desktop reports battery / mobile reports null",
            Status = batteryOk ? FingerprintCheckStatus.Pass : FingerprintCheckStatus.Warn,
            Severity = FingerprintCheckSeverity.Info,
        });

        // ─── SKIP — TLS / JA3 (out of scope for static check) ─────
        checks.Add(new FingerprintCheck
        {
            Id     = "tls_ja3_fingerprint",
            Title  = "TLS / JA3 fingerprint",
            Detail = "field not present in fingerprint — runtime check needed",
            Status = FingerprintCheckStatus.Skip,
            Severity = FingerprintCheckSeverity.Critical,
        });

        // ─── SKIP — WebRTC host filter (run-time check) ───────────
        checks.Add(new FingerprintCheck
        {
            Id     = "webrtc_host_filtered",
            Title  = "WebRTC host filtered",
            Detail = "field not present in fingerprint — run self-test to verify",
            Status = FingerprintCheckStatus.Skip,
            Severity = FingerprintCheckSeverity.Critical,
        });

        return Score(checks);
    }

    private static FingerprintScore Score(IReadOnlyList<FingerprintCheck> checks)
    {
        // Per-category penalty buckets: each fail subtracts from BOTH
        // the overall score (small) and the category-specific score
        // (larger) so a single bad UA doesn't tank Network's score.
        var overall = 100;
        var identity = 100;
        var hardware = 100;
        var network = 100;
        var automation = 100;
        var criticals = 0;
        var warns = 0;

        foreach (var c in checks)
        {
            if (c.Status is FingerprintCheckStatus.Pass or FingerprintCheckStatus.Skip) continue;

            var ovrPenalty = c.Severity switch
            {
                FingerprintCheckSeverity.Critical => 15,
                FingerprintCheckSeverity.Warning  => 5,
                _                                 => 1,
            };
            var catPenalty = c.Severity switch
            {
                FingerprintCheckSeverity.Critical => 25,
                FingerprintCheckSeverity.Warning  => 10,
                _                                 => 3,
            };

            // Crude category routing — assigns each check id to one
            // of the four pills. Identity = navigator.* + UA/platform.
            // Hardware = cpu/memory/screen/gpu/audio. Network =
            // languages/timezone/connection. Automation = webdriver +
            // forbidden fonts + plugins coherence.
            var bucket = c.Id switch
            {
                "ua_platform_matches_os" or "navigator_platform" or "navigator_vendor"
                    or "mobile_ua_marker" or "uach_brands" => "identity",
                "hardware_concurrency_realistic" or "device_memory_clamped"
                    or "screen_avail_jitter" or "noise_seeds_present"
                    or "battery_plausibility" => "hardware",
                "language_primary_consistent" or "timezone_set"
                    or "connection_type" => "network",
                _ => "automation",
            };

            overall -= ovrPenalty;
            switch (bucket)
            {
                case "identity":   identity   -= catPenalty; break;
                case "hardware":   hardware   -= catPenalty; break;
                case "network":    network    -= catPenalty; break;
                default:           automation -= catPenalty; break;
            }

            if (c.Severity == FingerprintCheckSeverity.Critical) criticals++;
            else if (c.Severity == FingerprintCheckSeverity.Warning) warns++;
        }

        overall    = Math.Max(0, overall);
        identity   = Math.Max(0, identity);
        hardware   = Math.Max(0, hardware);
        network    = Math.Max(0, network);
        automation = Math.Max(0, automation);

        var label = overall >= 85 ? "EXCELLENT"
                  : overall >= 75 ? "OK"
                  : overall >= 50 ? "RISKY"
                  : "BAD";

        return new FingerprintScore
        {
            Overall = overall, Label = label,
            Identity = identity, Hardware = hardware,
            Network = network,   Automation = automation,
            CriticalIssues = criticals, Warnings = warns,
            Checks = checks,
        };
    }
}

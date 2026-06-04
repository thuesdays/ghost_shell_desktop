// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhostShell.Core.Models;

namespace GhostShell.Runtime.Fingerprint;

/// <summary>
/// Builds the fingerprint payload our patched Chromium reads at
/// startup via the <c>--ghost-shell-payload=base64(json)</c> CLI flag.
/// Without this flag the C++ stealth patches never receive the
/// override values and the browser exposes its real hardware — sites
/// like Reddit instantly flag the inconsistency and block.
///
/// Port of the legacy <c>ghost_shell/fingerprint/device_templates.py</c>
/// <c>DeviceTemplateBuilder</c>. We keep the same payload shape and
/// the same field semantics so the patched-Chromium build can read
/// either (the patches don't know whether the JSON came from Python
/// or .NET).
///
/// Determinism: every random value is drawn from a per-profile
/// SHA-256(profile_name) seed. Two consecutive launches of the same
/// profile produce identical payloads — sites can't see "this profile
/// keeps changing its hardware between sessions".
///
/// To regenerate / reshuffle: pass a different <c>regenSalt</c> in the
/// ctor — that mixes into the seed and produces a fresh deterministic
/// payload (the user-driven "Regenerate" flow on the Fingerprint page).
/// </summary>
public sealed class DeviceTemplateBuilder
{
    public string ProfileName { get; }
    public DeviceTemplate Template { get; }
    public string Language { get; }
    public string TimezoneId { get; }
    public string ChromeMajor { get; }
    public string ChromeFull { get; }

    /// <summary>
    /// Effective OS used to drive every OS-coherent field. Normally equals
    /// the template's own <c>Os</c>, but coerces an impossible combination — a
    /// <see cref="FormFactor.Mobile"/> template left at the default
    /// <see cref="DeviceOs.Windows"/> — to <see cref="DeviceOs.Android"/>
    /// (there is no Windows phone). Callers that set FormFactor only (no
    /// explicit Os) therefore still get a coherent mobile fingerprint.
    /// </summary>
    public DeviceOs Os { get; }

    private readonly Random _rng;       // seeds main payload fields
    private readonly Random _noiseRng;  // seeds noise.* fields only
    private Dictionary<string, object?>? _cached;
    private string? _cachedJson;
    private string? _cachedB64;

    /// <summary>
    /// Build a fingerprint generator for one profile.
    /// </summary>
    /// <param name="profileName">DB profile name — drives the seed.</param>
    /// <param name="template">Selected device template (display fields).</param>
    /// <param name="language">Preferred Accept-Language; null → "en-US".</param>
    /// <param name="timezoneId">IANA timezone id; null → "Europe/Kyiv".</param>
    /// <param name="chromeMin">Lower bound for spoofed Chrome major; null.</param>
    /// <param name="chromeMax">Upper bound for spoofed Chrome major; null.</param>
    /// <param name="regenSalt">Optional salt to force regeneration —
    /// callers pass a UUID/timestamp to "regenerate" the payload while
    /// staying deterministic for that snapshot.</param>
    public DeviceTemplateBuilder(
        string profileName,
        DeviceTemplate template,
        string? language = null,
        string? timezoneId = null,
        string? chromeMin = null,
        string? chromeMax = null,
        string? regenSalt = null,
        string? noiseSalt = null)
    {
        ProfileName = profileName;
        Template    = template;
        Os          = ResolveOs(template);
        Language    = string.IsNullOrWhiteSpace(language) ? "en-US" : language;
        TimezoneId  = string.IsNullOrWhiteSpace(timezoneId) ? "Europe/Kyiv" : timezoneId;

        // TWO independent RNGs keyed off the same profile name but
        // different salts. The split is what makes "Reshuffle" work
        // correctly: bumping the noiseSalt alone re-rolls only the
        // canvas / WebGL / audio jitter, while regenSalt drives every
        // other field. Without this split, reshuffle would change
        // the entire payload (= same as regenerate).
        _rng      = MakeRng($"{profileName}|main|{regenSalt ?? ""}");
        _noiseRng = MakeRng($"{profileName}|noise|{regenSalt ?? ""}|{noiseSalt ?? ""}");

        var (major, full) = ChromeVersions.PickWeighted(_rng, chromeMin, chromeMax);
        ChromeMajor = major;
        ChromeFull  = full;
    }

    private static DeviceOs ResolveOs(DeviceTemplate t)
    {
        // A phone is never Windows. If a caller set FormFactor.Mobile but
        // left Os at its Windows default, treat it as Android. (Tablets are
        // left alone — Windows tablets like the Surface genuinely exist.)
        if (t.Os == DeviceOs.Windows && t.FormFactor == FormFactor.Mobile)
            return DeviceOs.Android;
        return t.Os;
    }

    private static Random MakeRng(string seedSrc)
    {
        var seedBytes = SHA256.HashData(Encoding.UTF8.GetBytes(seedSrc));
        return new Random(BitConverter.ToInt32(seedBytes, 0));
    }

    /// <summary>
    /// Returns the full payload as a Dictionary. Cached after first
    /// build — calling twice on the same instance is free.
    /// </summary>
    public Dictionary<string, object?> Build()
    {
        if (_cached is not null) return _cached;
        _cached = BuildInternal();
        return _cached;
    }

    /// <summary>JSON-serialised payload (compact, no whitespace).</summary>
    public string ToJson()
    {
        if (_cachedJson is not null) return _cachedJson;
        _cachedJson = JsonSerializer.Serialize(Build(), new JsonSerializerOptions
        {
            // Compact, no extra whitespace — matches legacy
            // json.dumps(separators=(',', ':')).
            WriteIndented = false,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return _cachedJson;
    }

    /// <summary>
    /// Base64 of the JSON payload — the value placed after
    /// <c>--ghost-shell-payload=</c> on the chrome.exe command line.
    /// </summary>
    public string ToBase64()
    {
        if (_cachedB64 is not null) return _cachedB64;
        _cachedB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(ToJson()));
        return _cachedB64;
    }

    /// <summary>The full Chrome CLI flag, ready for <c>options.AddArgument</c>.</summary>
    public string GetCliFlag() => $"--ghost-shell-payload={ToBase64()}";

    // ─────────────────────────────────────────────────────────────
    // Payload assembly
    // ─────────────────────────────────────────────────────────────

    private Dictionary<string, object?> BuildInternal()
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["version"]       = "3.1.0",
            ["profile_name"]  = ProfileName,
            ["template_name"] = Template.Id,
            ["hardware"]      = BuildHardware(),
            ["languages"]     = BuildLanguages(),
            ["screen"]        = BuildScreen(),
            ["graphics"]      = BuildGraphics(),
            ["gpu"]           = BuildGpu(),
            ["audio"]         = BuildAudio(),
            ["timezone"]      = BuildTimezone(),
            ["battery"]       = BuildBattery(),
            ["connection"]    = BuildConnection(),
            ["media"]         = BuildMediaDevices(),
            ["noise"]         = BuildNoise(),
            ["fonts"]         = BuildFonts(),
            ["ua_metadata"]   = BuildUaMetadata(),
            ["codecs"]        = BuildCodecs(),
            ["plugins"]       = BuildPlugins(),
            ["permissions"]   = BuildPermissions(),
        };
    }

    // ─── hardware ────────────────────────────────────────────────

    private Dictionary<string, object?> BuildHardware() => new()
    {
        ["user_agent"]            = BuildUserAgent(),
        ["platform"]              = TemplatePlatform(),
        ["hardware_concurrency"]  = ClampCpu(Template.CpuCores),
        ["device_memory"]         = ClampMemory(Template.RamGb),
        // audit FINGERPRINT-11: touch-capable form factors (phones AND
        // tablets) report touch points; desktops/laptops report 0.
        ["max_touch_points"]      = (Template.FormFactor is FormFactor.Mobile or FormFactor.Tablet) ? 5 : 0,
        ["pdf_viewer_enabled"]    = true,
    };

    private string BuildUserAgent()
    {
        // audit FINGERPRINT-01/03: UA is now driven by the explicit
        // Os single source of truth — NOT by FormFactor alone.
        // Previously every non-mobile template emitted a Windows UA (so
        // MacBooks/iPads looked like Windows) and every mobile template
        // emitted a hardcoded "Android 14; Pixel 7" UA (so iPhones became
        // Androids and all Samsung/Xiaomi reported a Pixel). The C++
        // patches read this verbatim and override navigator.userAgent.
        var maj = ChromeMajor;
        return Os switch
        {
            // Chrome on macOS reports "Intel Mac OS X 10_15_7" even on
            // Apple Silicon — Chrome froze this token years ago. Real.
            DeviceOs.MacOs =>
                $"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 " +
                $"(KHTML, like Gecko) Chrome/{maj}.0.0.0 Safari/537.36",

            DeviceOs.Android =>
                $"Mozilla/5.0 (Linux; Android 14; {AndroidModel()}) AppleWebKit/537.36 " +
                $"(KHTML, like Gecko) Chrome/{maj}.0.0.0 Mobile Safari/537.36",

            // iOS browsers are WebKit; Chrome-for-iOS uses the CriOS token.
            // NOTE: fully emulating iOS on a Blink engine is only partially
            // coherent (real iOS lacks navigator.userAgentData and exposes a
            // WebKit WebGL stack). We keep UA/platform/GPU/fonts internally
            // consistent so the gross "three OSes in one fingerprint" tell is
            // gone; deeper iOS parity needs a WebKit engine. See README/audit.
            DeviceOs.IOs => Template.FormFactor == FormFactor.Tablet
                ? $"Mozilla/5.0 (iPad; CPU OS 17_5 like Mac OS X) AppleWebKit/605.1.15 " +
                  $"(KHTML, like Gecko) CriOS/{maj}.0.0.0 Mobile/15E148 Safari/604.1"
                : $"Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 " +
                  $"(KHTML, like Gecko) CriOS/{maj}.0.0.0 Mobile/15E148 Safari/604.1",

            DeviceOs.Linux =>
                $"Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 " +
                $"(KHTML, like Gecko) Chrome/{maj}.0.0.0 Safari/537.36",

            _ =>
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                $"(KHTML, like Gecko) Chrome/{maj}.0.0.0 Safari/537.36",
        };
    }

    /// <summary>Android model token for the UA (e.g. "Pixel 8 Pro", "SM-S928B").</summary>
    private string AndroidModel() =>
        !string.IsNullOrWhiteSpace(Template.UaModel) ? Template.UaModel!
        : !string.IsNullOrWhiteSpace(Template.HumanName) ? Template.HumanName!
        : "Pixel 7";

    private string TemplatePlatform() => Os switch
    {
        // audit FINGERPRINT-01: navigator.platform must agree with the OS.
        DeviceOs.MacOs   => "MacIntel",        // Chrome reports MacIntel even on ARM Macs
        DeviceOs.Android => "Linux armv8l",
        DeviceOs.IOs     => Template.FormFactor == FormFactor.Tablet ? "iPad" : "iPhone",
        DeviceOs.Linux   => "Linux x86_64",
        _                => "Win32",
    };

    private static int ClampCpu(int requested)
    {
        // navigator.hardwareConcurrency is a defacto-standard discrete
        // value: 2/4/6/8/12/16/24/32. Weird values (3, 7, 10) are
        // themselves a fingerprint.
        var allowed = new[] { 2, 4, 6, 8, 12, 16, 24, 32 };
        if (requested <= 0) return 8;
        return allowed.OrderBy(a => Math.Abs(a - requested)).First();
    }

    private static double ClampMemory(double requestedGb)
    {
        // navigator.deviceMemory is documented to round to one of:
        // 0.25 / 0.5 / 1 / 2 / 4 / 8. Higher than 8 is reported as 8
        // in real Chrome (privacy-by-rounding). Match that behaviour.
        if (requestedGb <= 0) return 8;
        if (requestedGb >= 8) return 8;
        var allowed = new[] { 0.25, 0.5, 1.0, 2.0, 4.0, 8.0 };
        return allowed.OrderBy(a => Math.Abs(a - requestedGb)).First();
    }

    // ─── languages ───────────────────────────────────────────────

    private Dictionary<string, object?> BuildLanguages()
    {
        var primary = Language;
        var bare    = primary.Split('-')[0];
        var list    = new List<string> { primary };
        if (!string.Equals(bare, primary, StringComparison.Ordinal)) list.Add(bare);
        // Curated companion locales — matches legacy "expected reader"
        // pattern (a UA profile typically also has uk + ru + en).
        if (primary.StartsWith("uk", StringComparison.OrdinalIgnoreCase))
        {
            list.AddRange(new[] { "ru", "en-US", "en" });
        }
        else if (primary.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            list.AddRange(new[] { "en-US", "en" });
        }
        else if (!list.Contains("en-US")) list.Add("en-US");
        var unique = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Accept-Language with q= weights stepping down by 0.1.
        var acceptParts = new List<string>();
        var q = 1.0;
        foreach (var l in unique)
        {
            acceptParts.Add(q == 1.0 ? l : $"{l};q={q:0.0}");
            q = Math.Max(0.1, q - 0.1);
        }
        return new Dictionary<string, object?>
        {
            ["language"]        = primary,
            ["languages"]       = unique,
            ["accept_language"] = string.Join(",", acceptParts),
        };
    }

    // ─── screen ──────────────────────────────────────────────────

    private Dictionary<string, object?> BuildScreen()
    {
        var w = Template.ScreenWidth  > 0 ? Template.ScreenWidth  : 1920;
        var h = Template.ScreenHeight > 0 ? Template.ScreenHeight : 1080;
        var dpr = Template.Dpr > 0 ? Template.Dpr : 1.0;

        // audit FINGERPRINT-08: only desktop OSes have a taskbar/dock (the
        // availHeight gap) and a multi-monitor screen offset. Phones and
        // tablets fill the whole screen — availHeight == height and
        // screenX/screenY are 0. Emitting a desktop taskbar gap on a
        // "mobile" profile is an instant inconsistency.
        var isDesktop = Os is DeviceOs.Windows or DeviceOs.MacOs or DeviceOs.Linux;
        int availH, screenX, screenY;
        if (isDesktop)
        {
            availH  = h - _rng.Next(40, 60); // taskbar/dock height variance
            screenX = _rng.Next(0, 50);
            screenY = _rng.Next(0, 30);
        }
        else
        {
            availH  = h;
            screenX = 0;
            screenY = 0;
        }
        return new Dictionary<string, object?>
        {
            ["width"]         = w,
            ["height"]        = h,
            ["avail_height"]  = availH,
            ["color_depth"]   = 24,
            ["pixel_ratio"]   = dpr,
            ["screen_x"]      = screenX,
            ["screen_y"]      = screenY,
            ["orientation"]   = w >= h ? "landscape-primary" : "portrait-primary",
        };
    }

    // ─── graphics + gpu ──────────────────────────────────────────
    //
    // The patched Chromium reads two sibling sub-trees:
    //   • graphics — the WEBGL_debug_renderer_info strings that
    //     navigator.userAgentData and getParameter() expose
    //   • gpu — the higher-level "tier" (integrated / discrete) that
    //     governs feature gating (SwiftShader fallback, etc.)
    // Both are derived from Template.GpuModel.

    private Dictionary<string, object?> BuildGraphics()
    {
        var (vendor, renderer, webgpu) = ResolveGpuStrings();
        return new Dictionary<string, object?>
        {
            ["gl_vendor"]        = vendor,
            ["gl_renderer"]      = renderer,
            ["webgpu_vendor"]    = webgpu,
            ["webgpu_arch"]      = ResolveWebGpuArch(webgpu),
            // The C++ patch reads webgpu_device alongside vendor/arch.
            // Empty string is acceptable (real Chrome reports it empty
            // for adapters without a marketing device id) but the KEY
            // must be present — missing the key crashes the parse on
            // some patched builds.
            ["webgpu_device"]    = "",
            ["webgl_extensions"] = WebGlExtensions(),
        };
    }

    private Dictionary<string, object?> BuildGpu()
    {
        var (vendor, renderer, _) = ResolveGpuStrings();
        return new Dictionary<string, object?>
        {
            ["unmasked_vendor"]   = vendor,
            ["unmasked_renderer"] = renderer,
            ["tier"]              = ResolveGpuTier(),
        };
    }

    private (string Vendor, string Renderer, string WebGpu) ResolveGpuStrings()
    {
        var gpu   = (Template.GpuModel ?? "").ToLowerInvariant();
        var model = string.IsNullOrEmpty(Template.GpuModel) ? "" : Template.GpuModel!;

        // audit FINGERPRINT-10: Apple — distinguish macOS (Blink → ANGLE
        // Metal backend) from iOS (WebKit → generic "Apple GPU"). The old
        // code returned "Apple Apple M3 Max" (doubled prefix) for both.
        if (Os == DeviceOs.MacOs
            || gpu.Contains("apple") || gpu.Contains("m1") || gpu.Contains("m2") || gpu.Contains("m3"))
        {
            if (Os == DeviceOs.IOs)
                return ("Apple Inc.", "Apple GPU", "apple");
            var appleName = model.StartsWith("Apple", StringComparison.OrdinalIgnoreCase)
                ? model : $"Apple {model}".Trim();
            return ("Google Inc. (Apple)",
                    $"ANGLE (Apple, ANGLE Metal Renderer: {appleName}, Unspecified Version)",
                    "apple");
        }
        if (Os == DeviceOs.IOs)
            return ("Apple Inc.", "Apple GPU", "apple");

        // audit FINGERPRINT-02: Android/ARM mobile GPUs render via OpenGL ES
        // through ANGLE — NEVER Direct3D11 (a Windows-only API). A phone
        // reporting "...Direct3D11..." is an instant bot signal.
        if (gpu.Contains("mali"))
            return ("ARM", $"ANGLE (ARM, {model}, OpenGL ES 3.2)", "arm");
        if (gpu.Contains("adreno") || gpu.Contains("qualcomm"))
        {
            var num = new string(model.Where(char.IsDigit).ToArray());
            return ("Qualcomm",
                    $"ANGLE (Qualcomm, Adreno (TM) {(num.Length > 0 ? num : "740")}, OpenGL ES 3.2)",
                    "qualcomm");
        }

        // Desktop discrete / integrated (Windows / Linux, Direct3D11 / GL).
        // audit FINGERPRINT-06: the PCI device id now varies per GPU model
        // instead of a single constant shared by every NVIDIA/AMD/Intel card.
        if (gpu.Contains("nvidia") || gpu.Contains("geforce") || gpu.Contains("rtx"))
            return ("Google Inc. (NVIDIA)",
                    $"ANGLE (NVIDIA, NVIDIA {model} ({GpuDeviceId(gpu)}) Direct3D11 vs_5_0 ps_5_0, D3D11)",
                    "nvidia");
        if (gpu.Contains("amd") || gpu.Contains("radeon"))
            return ("Google Inc. (AMD)",
                    $"ANGLE (AMD, AMD {model} ({GpuDeviceId(gpu)}) Direct3D11 vs_5_0 ps_5_0, D3D11)",
                    "amd");

        // audit FINGERPRINT-02: an Android template that didn't match a known
        // mobile GPU must still resolve to ARM/GLES, never the Intel default.
        if (Os == DeviceOs.Android)
            return ("ARM", "ANGLE (ARM, Mali-G715, OpenGL ES 3.2)", "arm");

        // Default: Intel integrated (desktop Windows/Linux only).
        var intelName = string.IsNullOrEmpty(model) ? "UHD Graphics 770" : model;
        return ("Google Inc. (Intel)",
                $"ANGLE (Intel, Intel(R) {intelName} ({GpuDeviceId(gpu)}) Direct3D11 vs_5_0 ps_5_0, D3D11)",
                "intel");
    }

    /// <summary>
    /// audit FINGERPRINT-06: representative PCI device id per GPU family so
    /// the WebGL renderer string doesn't betray a single shared constant.
    /// Values approximate real device ids; fall back to a vendor default.
    /// </summary>
    private static string GpuDeviceId(string gpuLower)
    {
        var map = new (string Key, string Id)[]
        {
            ("rtx 4090",   "0x00002684"), ("rtx 4080",   "0x00002702"),
            ("rtx 4070 super", "0x00002783"), ("4060 laptop", "0x000028E0"),
            ("rtx 4070",   "0x00002786"), ("rtx 4060",   "0x00002882"),
            ("a4000",      "0x000024B0"),
            ("rx 7900",    "0x0000744C"), ("rx 6600",    "0x000073FF"),
            ("iris",       "0x00009A49"), ("arc",        "0x00007D55"),
            ("uhd graphics 770", "0x00004680"), ("uhd graphics", "0x00009BC4"),
        };
        foreach (var (key, id) in map)
            if (gpuLower.Contains(key)) return id;
        if (gpuLower.Contains("nvidia") || gpuLower.Contains("geforce") || gpuLower.Contains("rtx")) return "0x00002684";
        if (gpuLower.Contains("amd") || gpuLower.Contains("radeon")) return "0x0000744C";
        return "0x00009A60";
    }

    // audit WG-04: webgpu_vendor must be a canonical Chromium adapter vendor
    // token ("arm"/"qualcomm"/"apple"/"nvidia"/"amd"/"intel") — NOT "mali"
    // or "adreno" (those are device families, not WebGPU vendor strings).
    private static string ResolveWebGpuArch(string vendor) => vendor switch
    {
        "nvidia"   => "ada",
        "amd"      => "rdna3",
        "apple"    => "apple-gpu",
        "qualcomm" => "adreno",
        "arm"      => "valhall",
        _          => "xe",
    };

    private string ResolveGpuTier()
    {
        // Mobile/tablet (Android/iOS) GPUs are the "mobile" tier regardless
        // of the marketing model string.
        if (Os is DeviceOs.Android or DeviceOs.IOs
            || Template.FormFactor is FormFactor.Mobile or FormFactor.Tablet)
            return "mobile";
        var gpu = (Template.GpuModel ?? "").ToLowerInvariant();
        if (gpu.Contains("rtx 40") || gpu.Contains("rtx 50")) return "discrete_modern";
        if (gpu.Contains("rtx") || gpu.Contains("rx 7")) return "discrete_modern";
        if (gpu.Contains("intel") || gpu.Contains("apple")) return "integrated_modern";
        return "integrated_modern";
    }

    private static IReadOnlyList<string> WebGlExtensions() => new[]
    {
        "ANGLE_instanced_arrays", "EXT_blend_minmax", "EXT_color_buffer_half_float",
        "EXT_disjoint_timer_query", "EXT_float_blend", "EXT_frag_depth",
        "EXT_shader_texture_lod", "EXT_texture_compression_bptc",
        "EXT_texture_compression_rgtc", "EXT_texture_filter_anisotropic",
        "EXT_sRGB", "OES_element_index_uint", "OES_fbo_render_mipmap",
        "OES_standard_derivatives", "OES_texture_float", "OES_texture_float_linear",
        "OES_texture_half_float", "OES_texture_half_float_linear",
        "OES_vertex_array_object", "WEBGL_color_buffer_float",
        "WEBGL_compressed_texture_s3tc", "WEBGL_compressed_texture_s3tc_srgb",
        "WEBGL_debug_renderer_info", "WEBGL_debug_shaders", "WEBGL_depth_texture",
        "WEBGL_draw_buffers", "WEBGL_lose_context", "WEBGL_multi_draw",
    };

    // ─── audio ───────────────────────────────────────────────────

    private Dictionary<string, object?> BuildAudio()
    {
        // Real desktop audio: 48000 Hz, base latency varies a hair
        // between machines. We keep this within plausible range.
        return new Dictionary<string, object?>
        {
            ["sample_rate"]       = 48000,
            ["base_latency"]      = Math.Round(0.00800 + _rng.NextDouble() * 0.00050, 6),
            ["output_latency"]    = 0.0,
            ["max_channel_count"] = 2,
        };
    }

    // ─── timezone ────────────────────────────────────────────────

    private Dictionary<string, object?> BuildTimezone()
    {
        var (offset, str) = ResolveTimezoneOffset(TimezoneId);
        return new Dictionary<string, object?>
        {
            ["id"]         = TimezoneId,
            ["offset_min"] = offset,
            ["offset_str"] = str,
        };
    }

    private static (int OffsetMin, string OffsetStr) ResolveTimezoneOffset(string id)
    {
        try
        {
            // Cross-platform-ish: try IANA first, fall back to Windows.
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch
            {
                // Windows fallback for "Europe/Kyiv" → "FLE Standard Time" etc.
                tz = id switch
                {
                    "Europe/Kyiv" or "Europe/Kiev" => TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time"),
                    "America/New_York"             => TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"),
                    _ => TimeZoneInfo.Local,
                };
            }
            var offset = (int)tz.GetUtcOffset(DateTime.UtcNow).TotalMinutes;
            // navigator.timezoneOffset is INVERTED from POSIX convention.
            var posixSign = offset >= 0 ? "+" : "-";
            var hh = Math.Abs(offset) / 60;
            var mm = Math.Abs(offset) % 60;
            return (-offset, $"{posixSign}{hh:00}:{mm:00}");
        }
        catch
        {
            return (-180, "+03:00");
        }
    }

    // ─── battery / connection ────────────────────────────────────

    private object? BuildBattery()
    {
        // audit FINGERPRINT cluster: the Battery Status API is exposed by
        // Chrome on Android and by Blink on battery-equipped desktops
        // (Win/Mac/Linux laptops). It is NOT exposed on iOS (WebKit dropped
        // it) nor on desktops without a battery. An iPhone reporting a
        // battery object — or a Mac Studio reporting one — is a tell.
        var hasBattery = Os == DeviceOs.Android
                         || (Template.IsLaptop && Os != DeviceOs.IOs);
        if (hasBattery)
        {
            return new Dictionary<string, object?>
            {
                ["charging"]         = _rng.Next(0, 2) == 1,
                ["charging_time"]    = _rng.Next(0, 7200),
                ["discharging_time"] = _rng.Next(3600, 28800),
                ["level"]            = Math.Round(0.3 + _rng.NextDouble() * 0.7, 2),
            };
        }
        return null;
    }

    private Dictionary<string, object?> BuildConnection()
    {
        // audit FINGERPRINT-09: Chrome quantises NetworkInformation values
        // for privacy — rtt to the nearest 25 ms and downlink to the nearest
        // 0.05 Mbps (capped at 10). Emitting un-rounded values (e.g. rtt=73,
        // downlink=10.4) is off the grid real Chrome uses and is itself an
        // entropy tell.
        var rtt      = (int)(Math.Round(_rng.Next(40, 100) / 25.0) * 25);
        var downlink = Math.Min(10.0, Math.Round((8 + _rng.NextDouble() * 4) / 0.05) * 0.05);
        return new Dictionary<string, object?>
        {
            ["effective_type"] = "4g",
            ["downlink"]       = Math.Round(downlink, 2),
            ["rtt"]            = rtt,
            ["save_data"]      = false,
            ["type"]           = Template.FormFactor == FormFactor.Mobile ? "cellular" : "wifi",
        };
    }

    // ─── media devices ───────────────────────────────────────────

    private Dictionary<string, object?> BuildMediaDevices()
    {
        // Chrome enumerates these via navigator.mediaDevices.enumerateDevices().
        // The list is per-machine — we report a plausible default of
        // (one webcam, one mic, two output devices).
        return new Dictionary<string, object?>
        {
            ["video_inputs"]  = 1,
            ["audio_inputs"]  = 1,
            ["audio_outputs"] = 2,
        };
    }

    // ─── noise ───────────────────────────────────────────────────

    private Dictionary<string, object?> BuildNoise()
    {
        // All noise fields drawn from _noiseRng — keeps them in their
        // own seed bucket so "Reshuffle" can perturb noise alone.
        return new Dictionary<string, object?>
        {
            ["seed"]                    = _noiseRng.Next(),
            ["canvas_shift"]            = _noiseRng.Next(1, 8),
            ["canvas_noise"]            = Math.Round(_noiseRng.NextDouble() * 0.003, 6),
            ["webgl_noise"]             = Math.Round(_noiseRng.NextDouble() * 0.002, 6),
            ["webgl_params_mask"]       = _noiseRng.Next(0x3, 0x40),
            ["audio_offset"]            = Math.Round(_noiseRng.NextDouble() * 0.00027, 6),
            ["audio_rate_jitter"]       = _noiseRng.Next(-1, 2),
            ["rect_offset"]             = Math.Round(_noiseRng.NextDouble() * 0.019 + 0.001, 4),
            ["font_width_offset"]       = Math.Round((_noiseRng.NextDouble() - 0.5) * 3, 3),
            ["screen_avail_jitter"]     = _noiseRng.Next(0, 13),
            ["timezone_offset_jitter"]  = _noiseRng.Next(0, 5) == 0 ? _noiseRng.Next(-1, 2) : 0,
        };
    }

    // ─── fonts ───────────────────────────────────────────────────

    private IReadOnlyList<string> BuildFonts()
    {
        // audit FINGERPRINT-07: the installed-font list must match the OS.
        // The old code emitted the Windows core font set for EVERY profile,
        // so a "MacBook" or "iPhone" advertised Calibri/Segoe UI/MS Gothic —
        // fonts that don't exist on macOS/iOS/Android and instantly betray
        // the spoof. The C++ patches use this list to answer the font-probe
        // JS APIs (document.fonts.check, canvas text-metrics).
        switch (Os)
        {
            case DeviceOs.MacOs:
                return SampleFonts(MacCoreFonts, MacExtendedFonts);
            case DeviceOs.IOs:
                // iOS ships a fixed system font set — no user-installed fonts.
                return IosFonts.Distinct().ToList();
            case DeviceOs.Android:
                // Android exposes a tiny, fixed font set (Roboto/Noto family).
                return AndroidFonts.Distinct().ToList();
            case DeviceOs.Linux:
                return SampleFonts(LinuxCoreFonts, LinuxExtendedFonts);
            default:
                return SampleFonts(WindowsCoreFonts, WindowsExtendedFonts);
        }
    }

    /// <summary>core + a deterministic 10-15 sample of extended (desktop only).</summary>
    private IReadOnlyList<string> SampleFonts(string[] core, string[] extended)
    {
        var pickCount = _rng.Next(10, 16);
        var picks = extended.OrderBy(_ => _rng.Next()).Take(pickCount);
        return core.Concat(picks).Distinct().ToList();
    }

    private static readonly string[] WindowsCoreFonts =
    {
        "Arial", "Arial Black", "Calibri", "Cambria", "Cambria Math",
        "Candara", "Comic Sans MS", "Consolas", "Constantia", "Corbel",
        "Courier New", "Ebrima", "Franklin Gothic Medium", "Gabriola",
        "Georgia", "Impact", "Lucida Console", "Lucida Sans Unicode",
        "Microsoft Sans Serif", "MS Gothic", "MS PGothic", "Palatino Linotype",
        "Segoe Print", "Segoe Script", "Segoe UI", "Sylfaen", "Tahoma",
        "Times New Roman", "Trebuchet MS", "Verdana",
    };
    private static readonly string[] WindowsExtendedFonts =
    {
        "Bahnschrift", "Cascadia Code", "Cascadia Mono", "JetBrains Mono",
        "Inter", "Roboto", "Open Sans", "Source Sans Pro", "Source Code Pro",
        "Fira Code", "Fira Sans", "Helvetica Neue", "DejaVu Sans",
        "DejaVu Serif", "DejaVu Sans Mono",
    };
    private static readonly string[] MacCoreFonts =
    {
        "Helvetica", "Helvetica Neue", "Arial", "Arial Black", "Times",
        "Times New Roman", "Courier", "Courier New", "Georgia", "Verdana",
        "Trebuchet MS", "Comic Sans MS", "Impact", "Lucida Grande", "Geneva",
        "Menlo", "Monaco", "Palatino", "Optima", "Gill Sans", "Hoefler Text",
        "Baskerville", "Didot", "Futura", "Avenir", "Avenir Next",
        "American Typewriter", "Apple Color Emoji", "Apple SD Gothic Neo",
        "Hiragino Sans", "PingFang SC",
    };
    private static readonly string[] MacExtendedFonts =
    {
        "Arial Narrow", "Brush Script MT", "Chalkboard", "Cochin", "Copperplate",
        "Marker Felt", "Noteworthy", "Papyrus", "Phosphate", "Rockwell",
        "Savoye LET", "SignPainter", "Snell Roundhand", "Zapfino",
        "Andale Mono", "Big Caslon", "Bodoni 72", "Charter",
    };
    private static readonly string[] IosFonts =
    {
        "Helvetica", "Helvetica Neue", "Arial", "Times New Roman", "Georgia",
        "Courier New", "Verdana", "Trebuchet MS", "Avenir", "Avenir Next",
        "Palatino", "Marker Felt", "Snell Roundhand", "Apple Color Emoji",
        "Menlo", "Damascus", "Kohinoor",
    };
    private static readonly string[] AndroidFonts =
    {
        "Roboto", "Roboto Condensed", "Noto Sans", "Noto Serif",
        "Noto Sans Mono", "Noto Color Emoji", "Droid Sans", "Droid Serif",
        "Droid Sans Mono", "sans-serif", "monospace",
    };
    private static readonly string[] LinuxCoreFonts =
    {
        "DejaVu Sans", "DejaVu Serif", "DejaVu Sans Mono", "Liberation Sans",
        "Liberation Serif", "Liberation Mono", "Ubuntu", "Ubuntu Mono",
        "Noto Sans", "Noto Serif", "Cantarell", "FreeSans", "FreeSerif",
        "FreeMono", "Droid Sans",
    };
    private static readonly string[] LinuxExtendedFonts =
    {
        "Fira Sans", "Fira Code", "Source Code Pro", "Source Sans Pro",
        "Roboto", "Open Sans", "Inter", "JetBrains Mono", "Hack",
        "Cascadia Code", "Inconsolata", "Lato",
    };

    // ─── ua_metadata (Sec-CH-UA-* headers) ───────────────────────

    private Dictionary<string, object?> BuildUaMetadata()
    {
        // Sec-CH-UA hints. Order matters — Chrome always emits
        // "Not_A Brand" first, then "Chromium", then the visible
        // "Google Chrome" entry.
        //
        // Phase 60b — added `full_version_list`. Without this key the
        // C++ patch (ghost_shell_ua_override.cc:97 — `uam->FindList(
        // "full_version_list")`) leaves GhostShellUA::brand_full_version_list_
        // empty, the patched GetUserAgentMetadata() then falls through
        // to the upstream stub, and navigator.userAgentData.getHighEntropy
        // Values(['fullVersionList']) returns brands with versions that
        // get normalised to MAJOR.0.0.0 (Chrome's UA-Reduction shape).
        // Bot detectors flag "all 0.0.0.0" as a strong synthetic-browser
        // signal — real users have a genuine 4-part patch version like
        // 147.0.7780.88. Now we send the full version on every brand.
        return new Dictionary<string, object?>
        {
            ["brands"] = new object[]
            {
                new Dictionary<string, object?> { ["brand"] = "Not_A Brand", ["version"] = "8" },
                new Dictionary<string, object?> { ["brand"] = "Chromium",     ["version"] = ChromeMajor },
                new Dictionary<string, object?> { ["brand"] = "Google Chrome",["version"] = ChromeMajor },
            },
            // Phase 60b — Sec-CH-UA-Full-Version-List entries. Brand list
            // matches `brands` 1:1; versions are full four-component.
            // The "Not_A Brand" sibling stays on its static placeholder
            // "8.0.0.0" — that's what real Chrome ships. ChromeFull is
            // picked per profile from ChromeVersions.PickWeighted (e.g.
            // "147.0.7780.88").
            ["full_version_list"] = new object[]
            {
                new Dictionary<string, object?> { ["brand"] = "Not_A Brand", ["version"] = "8.0.0.0" },
                new Dictionary<string, object?> { ["brand"] = "Chromium",     ["version"] = ChromeFull },
                new Dictionary<string, object?> { ["brand"] = "Google Chrome",["version"] = ChromeFull },
            },
            ["full_version"]      = ChromeFull,
            // audit FINGERPRINT-01: Sec-CH-UA platform hints derive from the
            // OS source of truth so Sec-CH-UA-Platform / -Platform-Version /
            // -Arch agree with the UA and navigator.platform. (Real iOS does
            // not send UA-CH at all — emulating it on Blink is a known
            // residual limitation; we still keep the values self-consistent.)
            ["platform"]          = UaPlatformName(),
            ["platform_version"]  = UaPlatformVersion(),
            ["architecture"]      = Os == DeviceOs.Windows || Os == DeviceOs.Linux ? "x86" : "arm",
            ["bitness"]           = "64",
            ["mobile"]            = Template.FormFactor == FormFactor.Mobile,
            ["form_factor"]       = Template.FormFactor switch
            {
                FormFactor.Mobile => "mobile",
                FormFactor.Tablet => "tablet",
                _                 => "desktop",
            },
        };
    }

    private string UaPlatformName() => Os switch
    {
        DeviceOs.MacOs   => "macOS",
        DeviceOs.Android => "Android",
        DeviceOs.IOs     => "iOS",
        DeviceOs.Linux   => "Linux",
        _                => "Windows",
    };

    private string UaPlatformVersion() => Os switch
    {
        DeviceOs.MacOs   => "14.5.0",
        DeviceOs.Android => "14.0.0",
        DeviceOs.IOs     => "17.5.0",
        DeviceOs.Linux   => "6.5.0",
        _                => "15.0.0",
    };

    // ─── codecs ──────────────────────────────────────────────────

    private Dictionary<string, object?> BuildCodecs()
    {
        Dictionary<string, object?> Probe(bool supported) => new()
        {
            ["supported"]        = supported,
            ["smooth"]           = supported,
            ["power_efficient"]  = supported,
        };
        // Apple platforms ship hardware HEVC; Chrome on Windows/Linux/Android
        // typically reports h265 unsupported. Keep this OS-coherent.
        var hevc = Os is DeviceOs.MacOs or DeviceOs.IOs;
        return new Dictionary<string, object?>
        {
            ["av1"]  = Probe(true),
            ["vp9"]  = Probe(true),
            ["h264"] = Probe(true),
            ["h265"] = Probe(hevc),
        };
    }

    // ─── plugins ─────────────────────────────────────────────────

    private IReadOnlyList<object> BuildPlugins()
    {
        // Modern Chrome reports five PDF Viewer entries (one main
        // entry + four MIME aliases). Anything else is itself a tell.
        var entries = new[]
        {
            ("PDF Viewer",          "Portable Document Format"),
            ("Chrome PDF Viewer",   "Portable Document Format"),
            ("Chromium PDF Viewer", "Portable Document Format"),
            ("Microsoft Edge PDF Viewer", "Portable Document Format"),
            ("WebKit built-in PDF", "Portable Document Format"),
        };
        return entries.Select(e => (object)new Dictionary<string, object?>
        {
            ["name"]        = e.Item1,
            ["description"] = e.Item2,
            ["filename"]    = "internal-pdf-viewer",
        }).ToList();
    }

    // ─── permissions ─────────────────────────────────────────────

    private Dictionary<string, object?> BuildPermissions()
    {
        // navigator.permissions.query() default states. The C++ patch
        // overrides Permissions API to return these per-name.
        var defaults = new (string Name, string State)[]
        {
            ("notifications",         "default"),
            ("geolocation",           "prompt"),
            ("camera",                "prompt"),
            ("microphone",            "prompt"),
            ("midi",                  "prompt"),
            ("background-fetch",      "granted"),
            ("background-sync",       "granted"),
            ("persistent-storage",    "prompt"),
            ("clipboard-read",        "prompt"),
            ("clipboard-write",       "granted"),
            ("payment-handler",       "granted"),
            ("idle-detection",        "prompt"),
            ("periodic-background-sync", "prompt"),
            ("screen-wake-lock",      "prompt"),
            ("nfc",                   "prompt"),
            ("display-capture",       "prompt"),
            ("storage-access",        "prompt"),
            ("window-management",     "prompt"),
            ("bluetooth",             "prompt"),
            ("accelerometer",         "granted"),
        };
        var dict = new Dictionary<string, object?>();
        foreach (var (n, s) in defaults) dict[n] = s;
        return dict;
    }
}

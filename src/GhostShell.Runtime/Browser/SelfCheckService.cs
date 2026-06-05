// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Runtime.Fingerprint;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Browser;

/// <summary>
/// Concrete <see cref="ISelfCheckService"/>. Runs a comprehensive set of
/// probes against a live browser session and renders per-probe test results
/// as individual cards (matching the legacy web's 40-probe model).
///
/// Probe scope (expanded from legacy 4 probes):
///   • <b>Navigator</b> — userAgent, platform, language, languages[],
///     hardwareConcurrency, deviceMemory, maxTouchPoints, webdriver status
///   • <b>Screen</b> — width, height, availHeight, colorDepth, devicePixelRatio
///   • <b>Timezone</b> — Intl timezone ID, offset, locale
///   • <b>WebGL</b> — WEBGL_debug_renderer_info vendor + renderer strings
///   • <b>Canvas</b> — FNV-1a hash of a test render
///   • <b>Audio</b> — AudioContext sampleRate and baseLatency
///   • <b>Plugins</b> — plugin count and names
///   • <b>Network</b> — exit IP (from ipinfo.io), WebRTC leak detection
///   • <b>Automation</b> — webdriver exposure (should be hidden)
///
/// Each probe is compared against the expected value from the fingerprint
/// payload (via DeviceTemplateBuilder), producing a SelfCheckTestResult
/// with pass/warn/fail/skip status and severity levels.
///
/// Score: weighted average over non-skipped tests (pass = 1.0,
/// warn = 0.5, fail = 0), * 100, rounded — so benign warns don't
/// crater an otherwise-healthy profile and indeterminate "skip"
/// probes are excluded from the denominator (audit SELFCHECK-06).
/// Replaces the legacy ad-hoc 40/30/15/15 point allocation.
/// </summary>
public sealed class SelfCheckService : ISelfCheckService
{
    private readonly ISelfCheckHistoryService _history;
    private readonly IProfileService _profiles;
    private readonly ILogger<SelfCheckService> _log;

    public SelfCheckService(
        ISelfCheckHistoryService history,
        IProfileService profiles,
        ILogger<SelfCheckService> log)
    {
        _history  = history;
        _profiles = profiles;
        _log      = log;
    }

    public async Task<SelfCheckResult> RunAsync(
        IBrowserSession session, string profileName,
        long? runId = null, string? expectedTimezone = null,
        CancellationToken ct = default)
    {
        var ranAt = DateTime.UtcNow;
        string? exitIp = null, geoCountry = null, geoCity = null;
        string? tzActual = null, ua = null;
        bool webrtcLeak = false;
        string? webrtcLocalIp = null;
        // audit SELFCHECK-02: captured at function scope so the exit-IP geo
        // vs timezone/locale consistency probe (built in the network section
        // below) can correlate them. Populated inside the probe block.
        double? actualTzOffsetMin = null;
        string? actualNavLanguage = null;
        var notes = new List<string>();

        // Load the profile so we can build the expected fingerprint
        // payload. We need template ID, language, and the salts to
        // instantiate DeviceTemplateBuilder and get the expected values.
        var profile = await _profiles.GetAsync(profileName, ct);
        if (profile is null)
        {
            _log.LogWarning("Self-check profile '{P}' not found", profileName);
            notes.Add("profile not found");
        }

        // Build the expected fingerprint payload (if profile exists).
        // This gives us the reference values to compare JS probes against.
        Dictionary<string, object?>? expectedPayload = null;
        if (profile is not null)
        {
            try
            {
                // DeviceTemplateCatalog exposes a static `All` list keyed
                // by Id (no Instance/GetTemplate API — that was hallucinated
                // by the codegen pass). Lookup logic mirrors FingerprintService.
                // ResolveTemplate so the same template the runner used to
                // launch the browser is the one we compare against.
                var template = DeviceTemplateCatalog.All
                    .FirstOrDefault(t => string.Equals(
                        t.Id, profile.TemplateId, StringComparison.OrdinalIgnoreCase))
                    ?? DeviceTemplateCatalog.All.FirstOrDefault();
                if (template is null)
                {
                    notes.Add("no device templates available");
                    throw new InvalidOperationException("DeviceTemplateCatalog.All is empty");
                }

                var builder = new DeviceTemplateBuilder(
                    profileName: profileName,
                    template:    template,
                    language:    profile.Language,
                    timezoneId:  expectedTimezone,
                    chromeMin:   null,
                    chromeMax:   null,
                    regenSalt:   profile.FpRegenSalt,
                    noiseSalt:   profile.FpNoiseSalt);

                expectedPayload = builder.Build();
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Failed to build expected fingerprint for '{P}'", profileName);
                notes.Add("fingerprint build failed");
            }
        }

        // ─── Comprehensive JS probe script ──────────────────────────
        // Collects ~25 values from navigator, screen, timezone, WebGL,
        // canvas, audio, plugins, and webdriver into a single object.
        // Wrapped in async IIFE with error handling.
        const string ProbeScript = """
            return (async function() {
              try {
                const probes = {};

                // navigator
                probes.navigator = {
                  userAgent: navigator.userAgent,
                  platform: navigator.platform,
                  language: navigator.language,
                  languages: Array.from(navigator.languages || []),
                  hardwareConcurrency: navigator.hardwareConcurrency,
                  deviceMemory: navigator.deviceMemory,
                  maxTouchPoints: navigator.maxTouchPoints,
                  vendor: navigator.vendor,
                  doNotTrack: navigator.doNotTrack,
                  webdriver: !!navigator.webdriver,
                  cookieEnabled: navigator.cookieEnabled,
                };

                // screen
                probes.screen = {
                  width: window.screen.width,
                  height: window.screen.height,
                  availWidth: window.screen.availWidth,
                  availHeight: window.screen.availHeight,
                  colorDepth: window.screen.colorDepth,
                  pixelDepth: window.screen.pixelDepth,
                  devicePixelRatio: window.devicePixelRatio,
                };

                // timezone
                const tz = Intl.DateTimeFormat().resolvedOptions();
                probes.timezone = {
                  timeZone: tz.timeZone,
                  offsetMinutes: new Date().getTimezoneOffset(),
                };

                // WebGL vendor/renderer
                probes.webgl = { vendor: null, renderer: null };
                try {
                  const canvas = document.createElement('canvas');
                  const gl = canvas.getContext('webgl');
                  if (gl) {
                    const ext = gl.getExtension('WEBGL_debug_renderer_info');
                    if (ext) {
                      probes.webgl.vendor = gl.getParameter(ext.UNMASKED_VENDOR_WEBGL);
                      probes.webgl.renderer = gl.getParameter(ext.UNMASKED_RENDERER_WEBGL);
                    }
                  }
                } catch (e) {}

                // Canvas hash (FNV-1a of a test render)
                probes.canvas = { hash: null };
                try {
                  const canvas = document.createElement('canvas');
                  canvas.width = 100;
                  canvas.height = 40;
                  const ctx = canvas.getContext('2d');
                  if (ctx) {
                    ctx.font = '12px Arial';
                    ctx.fillText('hello,123!', 10, 20);
                    const imgData = ctx.getImageData(0, 0, 100, 40);
                    const data = imgData.data;
                    let hash = 2166136261; // FNV-1a offset basis (32-bit)
                    for (let i = 0; i < data.length; i++) {
                      hash = (hash >>> 0) ^ data[i];
                      hash = (hash * 16777619) >>> 0;
                    }
                    probes.canvas.hash = hash.toString(16);
                  }
                } catch (e) {}

                // Audio context
                probes.audio = { sampleRate: null, baseLatency: null };
                try {
                  const audioContext = new (window.AudioContext || window.webkitAudioContext)();
                  probes.audio.sampleRate = audioContext.sampleRate;
                  probes.audio.baseLatency = audioContext.baseLatency;
                } catch (e) {}

                // Plugins
                probes.plugins = {
                  count: navigator.plugins.length,
                  names: Array.from(navigator.plugins || []).map(p => p.name),
                };

                // Automation detection
                probes.automation = {
                  webdriver: !!navigator.webdriver,
                };

                // Return as JSON STRING — Selenium's object marshalling
                // of nested dictionaries is unpredictable across driver
                // versions (sometimes returns IDictionary<string,object>,
                // sometimes JObject, sometimes a flat string). Stringify
                // gives us a single deterministic shape to parse.
                return JSON.stringify(probes);
              } catch (e) {
                return JSON.stringify({ error: String(e) });
              }
            })();
        """;

        Dictionary<string, object?>? probeResults = null;
        try
        {
            var raw = await session.ExecuteScriptAsync(ProbeScript, null, ct);
            // Selenium can hand us EITHER a string (when our JS does
            // JSON.stringify) OR an already-deserialised dictionary
            // (some driver versions auto-parse the response). Handle
            // both — if it's a string, parse it; if it's a dict,
            // round-trip through JSON to get a uniform JsonElement
            // tree so the downstream extractors see the same shape.
            // Selenium driver versions differ:
            //   • Modern versions: JS object → string (since we
            //     JSON.stringify in the script).
            //   • Older versions: JS object → Dictionary<string,object>
            //     directly (auto-deserialise).
            //   • Some return JObject / ExpandoObject.
            // Coerce all of them to a JSON string by falling back to
            // JsonSerializer.Serialize — System.Text.Json handles
            // dictionaries, dynamics, and POCOs uniformly.
            string? jsonStr = raw switch
            {
                null      => null,
                string s  => s,
                _         => JsonSerializer.Serialize(raw),
            };
            if (!string.IsNullOrEmpty(jsonStr))
            {
                probeResults = JsonSerializer.Deserialize<Dictionary<string, object?>>(jsonStr);
                _log.LogInformation(
                    "Self-check probe for '{P}' collected {N} category groups",
                    profileName, probeResults?.Count ?? 0);
            }
            else
            {
                _log.LogWarning(
                    "Self-check probe for '{P}' returned null/empty (raw type: {T})",
                    profileName, raw?.GetType().FullName ?? "null");
                notes.Add("probe returned empty");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Comprehensive probe script failed for '{P}'", profileName);
            notes.Add("probe script failed: " + ex.Message);
        }

        // ─── Keep legacy probes (exit IP, WebRTC) ──────────────────
        // Exit IP + geo via ipinfo.io
        try
        {
            // audit SELFCHECK-07: cancel the in-flight fetch with an
            // AbortController when the per-request timeout fires (the old
            // code left a slow-but-healthy fetch running and could resolve
            // with a fake "timeout" error after the body arrived), and fall
            // back to a second geo provider so a single blocked/rate-limited
            // endpoint doesn't read as a network failure and tank the score.
            // Each provider's response is normalised to {ip, country, city}
            // so the C# parser below sees the same keys regardless of source.
            const string FetchJs = """
                return (async function() {
                  const fetchProvider = async (url, map) => {
                    const ctrl = new AbortController();
                    const timer = setTimeout(() => ctrl.abort(), 8000);
                    try {
                      const r = await fetch(url, {cache: 'no-store', signal: ctrl.signal});
                      const j = await r.json();
                      const out = map(j);
                      if (out && out.ip) return out;
                      return null;
                    } catch (e) {
                      return null;
                    } finally {
                      clearTimeout(timer);
                    }
                  };
                  // Primary: ipinfo.io  → already {ip, country, city}.
                  let res = await fetchProvider('https://ipinfo.io/json',
                    j => ({ ip: j.ip, country: j.country, city: j.city }));
                  // Fallback: ipapi.co → {ip, country_code, city}.
                  if (!res) {
                    res = await fetchProvider('https://ipapi.co/json/',
                      j => ({ ip: j.ip, country: j.country_code || j.country, city: j.city }));
                  }
                  if (res) return JSON.stringify(res);
                  return '{"_err":"all geo providers failed/timed out"}';
                })();
            """;
            var jsonText = await session.ExecuteScriptAsync(FetchJs, null, ct) as string;
            if (!string.IsNullOrWhiteSpace(jsonText))
            {
                try
                {
                    using var doc = JsonDocument.Parse(jsonText);
                    if (doc.RootElement.TryGetProperty("_err", out var errEl))
                    {
                        notes.Add("ipinfo fetch: " + (errEl.GetString() ?? "?"));
                    }
                    else
                    {
                        if (doc.RootElement.TryGetProperty("ip", out var ipEl))
                            exitIp = ipEl.GetString();
                        if (doc.RootElement.TryGetProperty("country", out var cEl))
                            geoCountry = cEl.GetString();
                        if (doc.RootElement.TryGetProperty("city", out var cyEl))
                            geoCity = cyEl.GetString();
                    }
                }
                catch (JsonException jx)
                {
                    var snippet = jsonText.Length > 80 ? jsonText[..80] + "…" : jsonText;
                    notes.Add("ipinfo body not JSON: " + snippet);
                    _log.LogDebug(jx, "ipinfo response wasn't JSON for '{P}': {Snippet}",
                        profileName, snippet);
                }
            }
            else
            {
                notes.Add("ipinfo.io returned no body");
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Self-check ipinfo probe failed for '{P}'", profileName);
            notes.Add("ipinfo probe failed: " + ex.Message);
        }

        // WebRTC leak detection
        const string WebRtcJs = """
            return new Promise((resolve) => {
              try {
                const pc = new RTCPeerConnection({iceServers: []});
                const ips = new Set();
                pc.createDataChannel('p');
                pc.onicecandidate = (e) => {
                  if (!e.candidate) {
                    pc.close();
                    resolve(Array.from(ips));
                    return;
                  }
                  // audit SELFCHECK-01: the old IPv4-only dotted-quad regex
                  // silently dropped every IPv6 candidate, so a global IPv6
                  // host/srflx address (routine on dual-stack hosts behind an
                  // IPv4-only proxy) was never evaluated and the leak read as
                  // "safe". Parse the SDP connection-address token directly so
                  // BOTH families — plus mDNS "*.local" obfuscation hostnames —
                  // reach the C# IsTrivialIp filter.
                  // candidate SDP: "candidate:<foundation> <component>
                  //   <transport> <priority> <conn-address> <port> typ ..."
                  // → field index 4 is the connection-address.
                  const cand = (e.candidate.candidate || '');
                  const parts = cand.split(' ');
                  if (parts.length > 4 && parts[4]) ips.add(parts[4]);
                };
                pc.createOffer({offerToReceiveAudio: true, offerToReceiveVideo: true})
                  .then((o) => pc.setLocalDescription(o))
                  .catch(() => resolve(Array.from(ips)));
                setTimeout(() => { try { pc.close(); } catch (e) {} resolve(Array.from(ips)); }, 2500);
              } catch (e) { resolve([]); }
            });
        """;
        try
        {
            var raw = await session.ExecuteScriptAsync(WebRtcJs, null, ct);
            if (raw is System.Collections.IEnumerable arr)
            {
                // audit WRTC-06: under the native-mDNS posture (WRTC-02) host
                // candidates MUST be "<uuid>.local" — a raw RFC1918 IPv4 host
                // candidate means mDNS isn't obfuscating (a regression that
                // external detectors catch but IsTrivialIp would mask as
                // "safe"). Track it and surface a note even though it isn't a
                // public-IP leak, so QA notices the mDNS posture broke.
                var sawRawPrivate = false;
                foreach (var ip in arr.Cast<object?>().Select(o => o?.ToString() ?? ""))
                {
                    if (string.IsNullOrEmpty(ip)) continue;
                    if (IsRawPrivateIpv4(ip)) sawRawPrivate = true;
                    if (IsTrivialIp(ip)) continue;
                    webrtcLeak = true;
                    webrtcLocalIp = ip;
                    break;
                }
                if (sawRawPrivate && !webrtcLeak)
                    notes.Add("webrtc: raw private IP visible — mDNS not obfuscating (WRTC-06)");
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "WebRTC probe failed for '{P}'", profileName);
            notes.Add("webrtc probe failed");
        }

        // ─── Build per-probe test results ──────────────────────────
        var tests = new List<SelfCheckTestResult>();

        // Phase 34 — dump the expected payload keys so we can see what
        // DeviceTemplateBuilder actually produced. Information level
        // (not Debug) so the user can grep app logs without flipping
        // the LogLevel filter every time we hunt a self-check FAIL.
        _log.LogInformation(
            "Self-check expected payload top-level keys: [{Keys}]",
            expectedPayload is not null
                ? string.Join(",", expectedPayload.Keys.OrderBy(k => k))
                : "(null)");
        // Drill into hardware / screen / audio / timezone too — these
        // are the sub-dicts we extract from in the comparison loop.
        // If any of these print "(null)" then DeviceTemplateBuilder
        // didn't emit that section and every test in it will fail
        // with empty Expected — that's a builder-side bug, not a
        // Chrome-patch bug. Knowing which is which saves debugging.
        foreach (var section in new[] { "hardware", "screen", "audio", "timezone", "languages", "graphics", "noise" })
        {
            var sub = ExtractDict(expectedPayload, section);
            _log.LogInformation(
                "Self-check payload['{Section}'] keys: [{Keys}]",
                section,
                sub is not null
                    ? string.Join(",", sub.Keys.OrderBy(k => k))
                    : "(null)");
        }

        if (probeResults is not null && !probeResults.ContainsKey("error"))
        {
            // Extract probe values from the nested structure
            var nav = ExtractDict(probeResults, "navigator");
            var scr = ExtractDict(probeResults, "screen");
            var tzProbe = ExtractDict(probeResults, "timezone");
            var wgl = ExtractDict(probeResults, "webgl");
            var cvs = ExtractDict(probeResults, "canvas");
            var aud = ExtractDict(probeResults, "audio");
            var plg = ExtractDict(probeResults, "plugins");
            var aut = ExtractDict(probeResults, "automation");

            // Extract expected values from the payload
            var hwPayload = ExtractDict(expectedPayload, "hardware");
            var langPayload = ExtractDict(expectedPayload, "languages");
            var scrPayload = ExtractDict(expectedPayload, "screen");
            var gfxPayload = ExtractDict(expectedPayload, "graphics");
            var audioPayload = ExtractDict(expectedPayload, "audio");
            var tzPayload = ExtractDict(expectedPayload, "timezone");
            // audit SELFCHECK-04: the "plugins" payload node is a top-level
            // List<object> (DeviceTemplateBuilder.BuildPlugins() returns
            // IReadOnlyList<object>), NOT a dictionary. ExtractDict therefore
            // always returned an empty dict and the count read 0. Read the
            // raw top-level value and count its elements via CountList.
            object? plgRaw = null;
            expectedPayload?.TryGetValue("plugins", out plgRaw);

            // navigator.userAgent
            var expectedUa = SafeGetAs<string>(hwPayload, "user_agent");
            var actualUa = SafeGetAs<string>(nav, "userAgent");
            // audit SELFCHECK-05: surface the captured UA to the row-level
            // RawJson + DB ua_actual column. Mirrors the tzActual = actualTzId
            // pattern below — the function-top `ua` was declared but never
            // assigned, so ua_actual was always null even on probe success.
            ua = actualUa;
            var uaStatus = StringMatch(expectedUa, actualUa) ? "pass" : "fail";
            if (uaStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'navigator.userAgent': expected='{E}' actual='{A}' → {Status} (severity=critical)",
                    expectedUa ?? "(null)", actualUa ?? "(null)", uaStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "navigator.userAgent",
                Label = "User-Agent",
                Category = "navigator",
                Status = uaStatus,
                Severity = "critical",
                Expected = expectedUa,
                Actual = actualUa,
                Detail = !StringMatch(expectedUa, actualUa)
                    ? $"Mismatch: expected Chrome version may differ from actual"
                    : null,
            });

            // navigator.platform
            var expectedPlat = SafeGetAs<string>(hwPayload, "platform");
            var actualPlat = SafeGetAs<string>(nav, "platform");
            tests.Add(new SelfCheckTestResult
            {
                Id = "navigator.platform",
                Label = "Platform",
                Category = "navigator",
                Status = StringMatch(expectedPlat, actualPlat) ? "pass" : "fail",
                Severity = "critical",
                Expected = expectedPlat,
                Actual = actualPlat,
            });

            // navigator.hardwareConcurrency — use SafeGetNumber so int /
            // long / JsonElement.Number all unbox correctly. Old code
            // did `as double?` which silently returned null for ints,
            // making EVERY numeric test fail with empty Expected.
            var expectedCpu = SafeGetNumber(hwPayload, "hardware_concurrency");
            var actualCpu = SafeGetNumber(nav, "hardwareConcurrency");
            var cpuStatus = NumericMatch(expectedCpu, actualCpu) ? "pass" : "fail";
            if (cpuStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'navigator.hardwareConcurrency': expected='{E}' actual='{A}' → {Status} (severity=important)",
                    expectedCpu?.ToString() ?? "(null)", actualCpu?.ToString() ?? "(null)", cpuStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "navigator.hardwareConcurrency",
                Label = "CPU Cores",
                Category = "navigator",
                Status = cpuStatus,
                Severity = "important",
                Expected = expectedCpu?.ToString(),
                Actual = actualCpu?.ToString(),
            });

            // navigator.deviceMemory
            var expectedMem = SafeGetNumber(hwPayload, "device_memory");
            var actualMem = SafeGetNumber(nav, "deviceMemory");
            tests.Add(new SelfCheckTestResult
            {
                Id = "navigator.deviceMemory",
                Label = "Device Memory (GB)",
                Category = "navigator",
                Status = NumericMatch(expectedMem, actualMem) ? "pass" : "fail",
                Severity = "important",
                Expected = expectedMem?.ToString(),
                Actual = actualMem?.ToString(),
            });

            // navigator.language
            var expectedLang = SafeGetAs<string>(langPayload, "language");
            var actualLang = SafeGetAs<string>(nav, "language");
            // audit SELFCHECK-02: stash for the geo/locale consistency probe.
            actualNavLanguage = actualLang;
            tests.Add(new SelfCheckTestResult
            {
                Id = "navigator.language",
                Label = "Language",
                Category = "navigator",
                Status = StringMatch(expectedLang, actualLang) ? "pass" : "fail",
                Severity = "important",
                Expected = expectedLang,
                Actual = actualLang,
            });

            // navigator.languages — both expected and actual can arrive
            // as multiple shapes: JsonElement (when serialised through
            // JSON), List<object> / ReadOnlyCollection<object> (Selenium
            // raw return), List<string>, string[], IEnumerable. The
            // helper below unifies them all into a single CSV string
            // for comparison. Without it the typed cast `as List<object>`
            // fails for ReadOnlyCollection (Selenium's actual return)
            // and the test reads null for both Expected AND Actual.
            // Bypass SafeGet for arrays — its switch only handles
            // JsonValueKind.{String,Number,True,False} and silently
            // returns null for Array kinds. Reading the dict value
            // directly preserves the JsonElement.Array (or List<string>
            // for the C#-side payload) so ToCsv can convert it. This
            // was the root cause of the persistent Languages Array FAIL:
            // the user's log showed `actual_raw_type='(null)'` because
            // SafeGet stripped the Array → null before ToCsv ever saw it.
            object? expectedLangsRaw = null;
            langPayload?.TryGetValue("languages", out expectedLangsRaw);
            object? actualLangsRaw = null;
            nav?.TryGetValue("languages", out actualLangsRaw);
            string? expectedLangs = ToCsv(expectedLangsRaw);
            string? actualLangs   = ToCsv(actualLangsRaw);
            _log.LogInformation(
                "Self-check probe 'navigator.languages': expected_raw_type='{ET}' expected_csv='{EC}' actual_raw_type='{AT}' actual_csv='{AC}'",
                expectedLangsRaw?.GetType().FullName ?? "(null)",
                expectedLangs ?? "(null)",
                actualLangsRaw?.GetType().FullName ?? "(null)",
                actualLangs ?? "(null)");
            tests.Add(new SelfCheckTestResult
            {
                Id = "navigator.languages",
                Label = "Languages Array",
                Category = "navigator",
                Status = StringMatch(expectedLangs, actualLangs) ? "pass" : "fail",
                Severity = "important",
                Expected = expectedLangs,
                Actual = actualLangs,
            });

            // navigator.webdriver (should be false / hidden)
            var actualWebdriver = SafeGet(nav, "webdriver") as bool?;
            var webdriverStatus = (actualWebdriver == false) ? "pass" : "fail";
            if (webdriverStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'navigator.webdriver': expected='false' actual='{A}' → {Status} (severity=critical)",
                    actualWebdriver?.ToString() ?? "(null)", webdriverStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "navigator.webdriver",
                Label = "Webdriver Hidden",
                Category = "automation",
                Status = webdriverStatus,
                Severity = "critical",
                Expected = "false",
                Actual = actualWebdriver?.ToString(),
                Detail = (actualWebdriver == true) ? "navigator.webdriver is exposed — stealth patch may have failed" : null,
            });

            // screen.width
            var expectedScrW = SafeGetNumber(scrPayload, "width");
            var actualScrW = SafeGetNumber(scr, "width");
            tests.Add(new SelfCheckTestResult
            {
                Id = "screen.width",
                Label = "Screen Width",
                Category = "screen",
                Status = NumericMatch(expectedScrW, actualScrW) ? "pass" : "fail",
                Severity = "important",
                Expected = expectedScrW?.ToString("F0"),
                Actual = actualScrW?.ToString("F0"),
            });

            // screen.height
            var expectedScrH = SafeGetNumber(scrPayload, "height");
            var actualScrH = SafeGetNumber(scr, "height");
            tests.Add(new SelfCheckTestResult
            {
                Id = "screen.height",
                Label = "Screen Height",
                Category = "screen",
                Status = NumericMatch(expectedScrH, actualScrH) ? "pass" : "fail",
                Severity = "important",
                Expected = expectedScrH?.ToString("F0"),
                Actual = actualScrH?.ToString("F0"),
            });

            // screen.colorDepth
            var expectedColorDepth = SafeGetNumber(scrPayload, "color_depth");
            var actualColorDepth = SafeGetNumber(scr, "colorDepth");
            var colorDepthStatus = NumericMatch(expectedColorDepth, actualColorDepth) ? "pass" : "warn";
            if (colorDepthStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'screen.colorDepth': expected='{E}' actual='{A}' → {Status} (severity=warning)",
                    expectedColorDepth?.ToString("F0") ?? "(null)", actualColorDepth?.ToString("F0") ?? "(null)", colorDepthStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "screen.colorDepth",
                Label = "Color Depth",
                Category = "screen",
                Status = colorDepthStatus,
                Severity = "warning",
                Expected = expectedColorDepth?.ToString("F0"),
                Actual = actualColorDepth?.ToString("F0"),
            });

            // screen.devicePixelRatio (with tolerance)
            var expectedDpr = SafeGetNumber(scrPayload, "pixel_ratio");
            var actualDpr = SafeGetNumber(scr, "devicePixelRatio");
            var dprMatch = FloatMatchWithTolerance(expectedDpr, actualDpr, 0.05);
            var dprStatus = dprMatch ? "pass" : "warn";
            if (dprStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'screen.devicePixelRatio': expected='{E}' actual='{A}' → {Status} (severity=warning)",
                    expectedDpr?.ToString("F2") ?? "(null)", actualDpr?.ToString("F2") ?? "(null)", dprStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "screen.devicePixelRatio",
                Label = "Pixel Ratio",
                Category = "screen",
                Status = dprStatus,
                Severity = "warning",
                Expected = expectedDpr?.ToString("F2"),
                Actual = actualDpr?.ToString("F2"),
            });

            // timezone.id — IANA aliases matter here. Chrome 149's
            // V8/ICU still reports "Europe/Kiev" (legacy) even when
            // the system / payload uses the modern "Europe/Kyiv";
            // both refer to the same physical zone. TimezoneMatch
            // canonicalises both sides so legitimate Ukrainian
            // profiles don't read as "FAIL · Europe/Kiev" forever.
            // Note also: there's NO C++ patch for Intl.DateTimeFormat
            // in the current ghost_shell_browser build — Chrome
            // resolves TZ from the system clock, not the payload.
            // If the user wants STRICT TZ override they need to add
            // an Intl patch upstream and rebuild Chromium; for now
            // we accept whatever Chrome reports as long as it's an
            // alias of the configured TZ.
            var expectedTzId = SafeGetAs<string>(tzPayload, "id");
            var actualTzId = SafeGetAs<string>(tzProbe, "timeZone");
            // audit SELFCHECK-02: stash the browser-reported UTC offset for
            // the geo/timezone consistency probe. getTimezoneOffset() returns
            // minutes BEHIND UTC (positive = west of UTC), the JS convention.
            actualTzOffsetMin = SafeGetNumber(tzProbe, "offsetMinutes");
            // Phase 71cc — surface the captured TZ to the row-level
            // log + DB column. Pre-fix the probe captured this value
            // for the test result but the function-top tzActual was
            // never assigned, so every Self-check row logged "tz=?"
            // and the DB column was null.
            tzActual = actualTzId;
            var tzOk = TimezoneMatch(expectedTzId, actualTzId);
            if (!tzOk)
            {
                _log.LogDebug(
                    "Self-check probe 'timezone.id': expected='{E}' actual='{A}' → fail (no alias match — Intl.DateTimeFormat not patched in Chromium build)",
                    expectedTzId ?? "(null)", actualTzId ?? "(null)");
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "timezone.id",
                Label = "Timezone",
                Category = "timezone",
                Status = tzOk ? "pass" : "fail",
                Severity = "critical",
                Expected = expectedTzId,
                Actual = actualTzId,
                Detail = tzOk ? null
                    : "Chrome's Intl returns the system TZ (no patch) — add an Intl.DateTimeFormat override in chromium/src or set the system clock TZ to the expected zone.",
            });

            // WebGL vendor (contains-test due to browser prefixing)
            var expectedGlVendor = SafeGetAs<string>(gfxPayload, "gl_vendor");
            var actualGlVendor = SafeGetAs<string>(wgl, "vendor");
            var glVendorMatch = expectedGlVendor is not null && actualGlVendor is not null
                && (actualGlVendor.Contains(expectedGlVendor, StringComparison.OrdinalIgnoreCase)
                    || expectedGlVendor.Contains(actualGlVendor, StringComparison.OrdinalIgnoreCase));
            // audit SELFCHECK-06: a value we COULDN'T read is "skip" (excluded
            // from the score), never "pass". The old `|| actualGlVendor is null
            // ? pass` let an unreadable WebGL probe score better than one that
            // ran and mismatched — the opposite of what a self-check should do.
            var glVendorStatus = actualGlVendor is null ? "skip"
                : glVendorMatch ? "pass" : "warn";
            if (glVendorStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'webgl.vendor': expected='{E}' actual='{A}' → {Status} (severity=warning)",
                    expectedGlVendor ?? "(null)", actualGlVendor ?? "(null)", glVendorStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "webgl.vendor",
                Label = "WebGL Vendor",
                Category = "webgl",
                Status = glVendorStatus,
                Severity = "warning",
                Expected = expectedGlVendor,
                Actual = actualGlVendor,
            });

            // WebGL renderer
            var expectedGlRenderer = SafeGetAs<string>(gfxPayload, "gl_renderer");
            var actualGlRenderer = SafeGetAs<string>(wgl, "renderer");
            var glRendererMatch = expectedGlRenderer is not null && actualGlRenderer is not null
                && (actualGlRenderer.Contains(expectedGlRenderer, StringComparison.OrdinalIgnoreCase)
                    || expectedGlRenderer.Contains(actualGlRenderer, StringComparison.OrdinalIgnoreCase));
            // audit SELFCHECK-06: unreadable → "skip" (excluded), not "pass".
            var glRendererStatus = actualGlRenderer is null ? "skip"
                : glRendererMatch ? "pass" : "warn";
            if (glRendererStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'webgl.renderer': expected='{E}' actual='{A}' → {Status} (severity=warning)",
                    expectedGlRenderer ?? "(null)", actualGlRenderer ?? "(null)", glRendererStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "webgl.renderer",
                Label = "WebGL Renderer",
                Category = "webgl",
                Status = glRendererStatus,
                Severity = "warning",
                Expected = expectedGlRenderer,
                Actual = actualGlRenderer,
            });

            // Canvas hash (presence test)
            var actualCanvasHash = SafeGetAs<string>(cvs, "hash");
            tests.Add(new SelfCheckTestResult
            {
                Id = "canvas.hash",
                Label = "Canvas Fingerprint",
                Category = "canvas",
                Status = !string.IsNullOrEmpty(actualCanvasHash) && actualCanvasHash != "0" ? "pass" : "skip",
                Severity = "info",
                Expected = "(hash present)",
                Actual = !string.IsNullOrEmpty(actualCanvasHash) ? "(computed)" : "(unavailable)",
            });

            // Audio sampleRate — the C++ patch INTENTIONALLY adds a
            // ±1 Hz jitter (noise.audio_rate_jitter) on top of the base
            // sample rate so two profiles never expose the exact same
            // value. The self-check must read the jitter from the
            // payload and treat |actual − (base + jitter)| ≤ 1 as pass.
            // Without this allowance every profile would show "warn ·
            // 48001" forever even when the patch is working correctly.
            var expectedSampleRate = SafeGetNumber(audioPayload, "sample_rate");
            var actualSampleRate = SafeGetNumber(aud, "sampleRate");
            var noisePayload = ExtractDict(expectedPayload, "noise");
            var jitter = SafeGetNumber(noisePayload, "audio_rate_jitter") ?? 0;
            // tolerance window: jitter span is [-1, +1] from BuildNoise,
            // plus 0.5 Hz fp slack so the rounded JS double matches.
            var audioOk = expectedSampleRate.HasValue && actualSampleRate.HasValue
                && Math.Abs(actualSampleRate.Value - (expectedSampleRate.Value + jitter)) < 1.5;
            var audioStatus = audioOk ? "pass" : "warn";
            if (audioStatus != "pass")
            {
                _log.LogDebug(
                    "Self-check probe 'audio.sampleRate': expected_base='{E}' jitter='{J}' actual='{A}' → {Status}",
                    expectedSampleRate?.ToString("F0") ?? "(null)",
                    jitter,
                    actualSampleRate?.ToString("F0") ?? "(null)",
                    audioStatus);
            }
            tests.Add(new SelfCheckTestResult
            {
                Id = "audio.sampleRate",
                Label = "Audio Sample Rate",
                Category = "audio",
                Status = audioStatus,
                Severity = "warning",
                // Show the expected jittered range so the user sees
                // why 48001 is acceptable when base is 48000.
                Expected = expectedSampleRate.HasValue
                    ? $"{expectedSampleRate.Value:F0}±1"
                    : null,
                Actual = actualSampleRate?.ToString("F0"),
                Detail = audioOk ? null
                    : $"out of jittered range; payload jitter={jitter}",
            });

            // Plugins count
            // audit SELFCHECK-04: count the real top-level plugins list and
            // perform a genuine comparison. The previous `actual == expected
            // ? "pass" : "pass"` ternary was dead code (both branches pass)
            // sitting on top of an always-0 expected — a zero-plugin headless
            // build (a classic bot tell) would have read as fine. Plugin set
            // is informational (Chrome's PDF plugin shim can legitimately
            // vary), so a mismatch is reported as "warn", not a hard fail.
            var expectedPlugCount = CountList(plgRaw);
            var actualPlugCount = SafeGetNumber(plg, "count") ?? 0;
            tests.Add(new SelfCheckTestResult
            {
                Id = "plugins.count",
                Label = "Plugin Count",
                Category = "fonts",
                Status = (int)actualPlugCount == expectedPlugCount ? "pass" : "warn",
                Severity = "info",
                Expected = expectedPlugCount.ToString(),
                // actualPlugCount is now `double` (non-nullable) since
                // SafeGetNumber(...) ?? 0 collapses null to 0. Use the
                // direct ToString call — null-conditional `?` would
                // fail to compile against a non-nullable value type.
                Actual = actualPlugCount.ToString("F0"),
            });
        }

        // Add network tests (exit IP, WebRTC)
        // audit SELFCHECK-07: a missing exit IP is "warn", not a hard "fail".
        // Both geo providers being blocked/rate-limited by the proxy does NOT
        // mean the connection is broken — it's an indeterminate probe, so it
        // should not be scored as a clean failure (see SELFCHECK-06).
        tests.Add(new SelfCheckTestResult
        {
            Id = "network.exit_ip",
            Label = "Exit IP",
            Category = "network",
            Status = !string.IsNullOrEmpty(exitIp) ? "pass" : "warn",
            Severity = "info",
            Expected = "(should be populated)",
            Actual = exitIp ?? "(none)",
            Detail = !string.IsNullOrEmpty(exitIp) ? null
                : "Could not determine exit IP — all geo providers timed out or were blocked by the proxy; the connection itself may still be fine.",
        });

        tests.Add(new SelfCheckTestResult
        {
            Id = "network.webrtc_leak",
            Label = "WebRTC Leak",
            Category = "network",
            Status = !webrtcLeak ? "pass" : "fail",
            Severity = "critical",
            Expected = "(no leak)",
            Actual = webrtcLeak ? $"Leaked IP: {webrtcLocalIp}" : "(safe)",
        });

        // audit SELFCHECK-02: the canonical antidetect consistency probe —
        // does the exit IP's country agree with the browser-reported timezone
        // and locale? Anti-bot stacks compute exactly this correlation; a
        // mismatch (e.g. a Brazil exit IP while the browser reports
        // America/New_York / en-US) is a high-confidence proxy/bot signal.
        // We compare against the LIVE browser values (actual TZ offset + nav
        // language), not the payload, because Chrome's Intl is not patched in
        // this build (see timezone.id note) so the offset is the system clock.
        // tzActual mirrors the in-block actualTzId (assigned at the timezone.id
        // probe); it is the function-scope copy reachable here.
        AddGeoConsistencyTests(tests, geoCountry, tzActual, actualTzOffsetMin, actualNavLanguage);

        // audit SELFCHECK-06: weighted score with partial credit so benign
        // "warn" outcomes (colorDepth, pixelRatio, webgl, audio jitter, locale)
        // don't push a healthy profile into a RISKY band, and "skip" results
        // (canvas unavailable, unreadable WebGL, indeterminate geo checks) are
        // excluded from the denominator entirely rather than counted as
        // failures. pass = 1.0, warn = 0.5, fail = 0; rounded (not truncated).
        double Weight(string status) => status switch
        {
            "pass" => 1.0,
            "warn" => 0.5,
            _      => 0.0,   // "fail" (and any unexpected status)
        };
        var scored = tests.Where(t => t.Status != "skip").ToList();
        var earned = scored.Sum(t => Weight(t.Status));
        var score = scored.Count > 0
            ? (int)Math.Round(earned * 100.0 / scored.Count, MidpointRounding.AwayFromZero)
            : 0;

        // Serialize tests to JSON
        var testsJson = JsonSerializer.Serialize(tests, new JsonSerializerOptions { WriteIndented = false });

        var raw_ = JsonSerializer.Serialize(new
        {
            exit_ip = exitIp, geo_country = geoCountry, geo_city = geoCity,
            webrtc_leaked = webrtcLeak, webrtc_local_ip = webrtcLocalIp,
            timezone_actual = tzActual, timezone_expected = expectedTimezone,
            ua_actual = ua,
            notes,
        });

        var result = new SelfCheckResult
        {
            ProfileName       = profileName,
            RunId             = runId,
            RanAt             = ranAt,
            ExitIp            = exitIp,
            GeoCountry        = geoCountry,
            GeoCity           = geoCity,
            WebRtcLeaked      = webrtcLeak,
            WebRtcLocalIp     = webrtcLocalIp,
            TimezoneActual    = tzActual,
            TimezoneExpected  = expectedTimezone,
            UaActual          = ua,
            Score             = score,
            Note              = notes.Count == 0 ? null : string.Join("; ", notes),
            RawJson           = raw_,
            TestsJson         = testsJson,
        };
        var id = await _history.InsertAsync(result, ct);
        return result with { Id = id };
    }

    public Task<IReadOnlyList<SelfCheckResult>> ListAsync(
        string profileName, int limit = 50, CancellationToken ct = default)
        => _history.ListAsync(profileName, limit, ct);

    public Task<SelfCheckResult?> GetLatestAsync(string profileName, CancellationToken ct = default)
        => _history.GetLatestAsync(profileName, ct);

    /// <summary>
    /// audit SELFCHECK-02: country (ISO-3166 alpha-2) → inclusive band of
    /// acceptable UTC offsets in HOURS, covering the country's full span of
    /// civil timezones across standard time AND DST. A reported offset that
    /// falls outside the band for the exit-IP country is a geo inconsistency.
    /// We ship the antidetect-relevant countries; unmapped countries yield a
    /// "skip" (indeterminate) rather than a false positive.
    /// </summary>
    private static readonly Dictionary<string, (double Min, double Max)> _countryOffsetBands =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = (-10, -4),   // Hawaii (-10) .. Eastern DST (-4)
        ["CA"] = (-8, -2.5),  // Pacific .. Newfoundland DST
        ["GB"] = (0, 1),      // GMT / BST
        ["IE"] = (0, 1),
        ["PT"] = (-1, 1),     // mainland + Azores
        ["FR"] = (0, 2),      // mainland CET/CEST (overseas excluded)
        ["DE"] = (1, 2),
        ["NL"] = (1, 2),
        ["ES"] = (0, 2),      // Canaries .. mainland CEST
        ["IT"] = (1, 2),
        ["PL"] = (1, 2),
        ["UA"] = (2, 3),      // EET / EEST
        ["RU"] = (2, 12),     // Kaliningrad .. Kamchatka
        ["BR"] = (-5, -2),    // Acre .. Fernando de Noronha
        ["AR"] = (-3, -3),
        ["MX"] = (-8, -5),
        ["IN"] = (5.5, 5.5),
        ["CN"] = (8, 8),
        ["JP"] = (9, 9),
        ["KR"] = (9, 9),
        ["AU"] = (8, 11),     // AWST .. AEDT
        ["NZ"] = (12, 13),
        ["SG"] = (8, 8),
        ["HK"] = (8, 8),
        ["AE"] = (4, 4),
        ["TR"] = (3, 3),
        ["ZA"] = (2, 2),
        ["VN"] = (7, 7),
        ["ID"] = (7, 9),
        ["TH"] = (7, 7),
        ["PH"] = (8, 8),
    };

    /// <summary>
    /// audit SELFCHECK-02: leading region/country subtags that are expected
    /// for a given exit-IP country, used for the navigator.language vs geo
    /// cross-check. Keyed by ISO-3166 country; values are acceptable
    /// BCP-47 region subtags (the part after '-' in e.g. "en-US"). Where a
    /// language has no region subtag we cannot judge, so it is not failed.
    /// </summary>
    private static readonly Dictionary<string, string[]> _countryLanguageRegions =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = new[] { "US" },
        ["CA"] = new[] { "CA" },
        ["GB"] = new[] { "GB", "UK" },
        ["IE"] = new[] { "IE", "GB" },
        ["FR"] = new[] { "FR" },
        ["DE"] = new[] { "DE", "AT" },
        ["NL"] = new[] { "NL" },
        ["ES"] = new[] { "ES" },
        ["IT"] = new[] { "IT" },
        ["PL"] = new[] { "PL" },
        ["PT"] = new[] { "PT" },
        ["UA"] = new[] { "UA", "RU" },
        ["RU"] = new[] { "RU" },
        ["BR"] = new[] { "BR" },
        ["AR"] = new[] { "AR" },
        ["MX"] = new[] { "MX" },
        ["IN"] = new[] { "IN" },
        ["CN"] = new[] { "CN" },
        ["JP"] = new[] { "JP" },
        ["KR"] = new[] { "KR" },
        ["AU"] = new[] { "AU" },
        ["NZ"] = new[] { "NZ" },
        ["TR"] = new[] { "TR" },
        ["ZA"] = new[] { "ZA" },
        ["VN"] = new[] { "VN" },
        ["TH"] = new[] { "TH" },
        ["PH"] = new[] { "PH" },
    };

    /// <summary>
    /// audit SELFCHECK-02: emit the exit-IP geo vs timezone/locale
    /// consistency tests. These are the single most important antidetect
    /// correlation checks and were previously missing entirely. Each sub-test
    /// is "skip" (excluded from the score, see SELFCHECK-06) when we lack the
    /// inputs to judge — we never raise a false leak alarm on missing data.
    /// </summary>
    private static void AddGeoConsistencyTests(
        List<SelfCheckTestResult> tests,
        string? geoCountry,
        string? actualTzId,
        double? tzOffsetMin,
        string? navLanguage)
    {
        // ── exit-IP country vs reported timezone offset ──
        string tzStatus;
        string? tzDetail = null;
        string tzActualStr;
        string tzExpectedStr;

        if (string.IsNullOrWhiteSpace(geoCountry) || tzOffsetMin is null)
        {
            tzStatus = "skip";
            tzExpectedStr = "(needs exit-IP country + TZ offset)";
            tzActualStr = tzOffsetMin is null
                ? "(no TZ offset)"
                : $"country=(none) offset={FmtOffset(-tzOffsetMin.Value / 60.0)}";
        }
        else if (!_countryOffsetBands.TryGetValue(geoCountry, out var band))
        {
            // We don't have an offset band for this country — indeterminate.
            tzStatus = "skip";
            tzExpectedStr = $"(no offset band for '{geoCountry}')";
            tzActualStr = $"{actualTzId ?? "?"} ({FmtOffset(-tzOffsetMin.Value / 60.0)})";
        }
        else
        {
            // getTimezoneOffset(): positive = behind UTC. UTC offset = -mins/60.
            var utcOffsetHours = -tzOffsetMin.Value / 60.0;
            // 0.25h slack for half-hour/quarter-hour zones rounding.
            var ok = utcOffsetHours >= band.Min - 0.25 && utcOffsetHours <= band.Max + 0.25;
            tzStatus = ok ? "pass" : "fail";
            tzExpectedStr = $"{geoCountry}: UTC{FmtOffset(band.Min)}..UTC{FmtOffset(band.Max)}";
            tzActualStr = $"{actualTzId ?? "?"} (UTC{FmtOffset(utcOffsetHours)})";
            if (!ok)
                tzDetail = $"Exit IP geolocates to '{geoCountry}' but the browser timezone " +
                           $"offset UTC{FmtOffset(utcOffsetHours)} is outside that country's " +
                           $"band — a high-confidence proxy/timezone-mismatch signal.";
        }

        tests.Add(new SelfCheckTestResult
        {
            Id = "network.geo_timezone_consistency",
            Label = "Geo / Timezone Consistency",
            Category = "network",
            Status = tzStatus,
            Severity = "critical",
            Expected = tzExpectedStr,
            Actual = tzActualStr,
            Detail = tzDetail,
        });

        // ── exit-IP country vs navigator.language region subtag ──
        string langStatus;
        string? langDetail = null;
        string langActualStr = navLanguage ?? "(none)";
        string langExpectedStr;

        var region = ExtractRegionSubtag(navLanguage);
        if (string.IsNullOrWhiteSpace(geoCountry)
            || !_countryLanguageRegions.TryGetValue(geoCountry, out var okRegions))
        {
            langStatus = "skip";
            langExpectedStr = string.IsNullOrWhiteSpace(geoCountry)
                ? "(needs exit-IP country)"
                : $"(no locale map for '{geoCountry}')";
        }
        else if (region is null)
        {
            // Language has no region subtag (e.g. "en") — cannot judge.
            langStatus = "skip";
            langExpectedStr = $"{geoCountry}: lang region in [{string.Join(",", okRegions)}]";
        }
        else
        {
            var ok = okRegions.Contains(region, StringComparer.OrdinalIgnoreCase);
            // Locale mismatch is a softer signal than TZ (travellers, expats
            // legitimately run e.g. en-US in another country) → "warn".
            langStatus = ok ? "pass" : "warn";
            langExpectedStr = $"{geoCountry}: lang region in [{string.Join(",", okRegions)}]";
            if (!ok)
                langDetail = $"Exit IP country '{geoCountry}' but navigator.language region " +
                             $"is '{region}' — locale/geo mismatch worth reviewing.";
        }

        tests.Add(new SelfCheckTestResult
        {
            Id = "network.geo_locale_consistency",
            Label = "Geo / Locale Consistency",
            Category = "network",
            Status = langStatus,
            Severity = "warning",
            Expected = langExpectedStr,
            Actual = langActualStr,
            Detail = langDetail,
        });
    }

    /// <summary>
    /// audit SELFCHECK-02: extract the BCP-47 region subtag (the 2-letter
    /// country code after the language) from a navigator.language value such
    /// as "en-US" → "US" or "pt-BR" → "BR". Returns null when absent.
    /// </summary>
    private static string? ExtractRegionSubtag(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        var parts = lang.Split('-', '_');
        // Walk subtags; the region is a 2-letter (alpha) subtag after the
        // primary language. (3-digit UN M.49 region codes exist but are rare
        // in navigator.language and not in our country maps.)
        for (var i = 1; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p.Length == 2 && char.IsLetter(p[0]) && char.IsLetter(p[1]))
                return p.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>
    /// Format a UTC offset in hours as a signed string, e.g. -4 → "-04:00",
    /// 5.5 → "+05:30". Used by the geo/timezone consistency probe output.
    /// </summary>
    private static string FmtOffset(double hours)
    {
        var sign = hours < 0 ? "-" : "+";
        var abs = Math.Abs(hours);
        var h = (int)abs;
        var m = (int)Math.Round((abs - h) * 60);
        return $"{sign}{h:D2}:{m:D2}";
    }

    /// <summary>
    /// IPs/hostnames that DON'T constitute a leak: loopback, link-local,
    /// private LAN ranges, and mDNS obfuscation hostnames. We treat private
    /// LAN IPs (10/8, 192.168/16, 172.16/12) as "trivial" because modern
    /// Chrome's mDNS obfuscation hides them by default — if we see one it's
    /// our patched build skipping the obfuscation, not the user's real WAN
    /// address.
    ///
    /// audit SELFCHECK-01: this now also classifies IPv6 candidates. A
    /// *global* IPv6 address (anything not loopback / link-local / ULA /
    /// mDNS) is a genuine identity leak on a dual-stack host behind an
    /// IPv4-only proxy and MUST return false here so the WebRTC test fails.
    /// </summary>
    /// <summary>audit WRTC-06: true for a raw RFC1918 IPv4 literal (10/8,
    /// 172.16/12, 192.168/16). Under the native-mDNS posture seeing one means
    /// mDNS failed to obfuscate the host candidate — a regression worth a note.
    /// Distinct from <see cref="IsTrivialIp"/>, which (correctly) treats these
    /// as non-leaks of public information.</summary>
    private static bool IsRawPrivateIpv4(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip.Contains(':')) return false;
        if (ip.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return false;
        if (ip.StartsWith("10.")) return true;
        if (ip.StartsWith("192.168.")) return true;
        if (ip.StartsWith("172."))
        {
            var parts = ip.Split('.');
            if (parts.Length > 1 && int.TryParse(parts[1], out var second)
                && second is >= 16 and <= 31)
                return true;
        }
        return false;
    }

    private static bool IsTrivialIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return true;

        // mDNS obfuscation hostnames (e.g. "<uuid>.local") — Chrome's
        // default privacy mechanism; never a real-address leak.
        if (ip.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) return true;

        // IPv6 candidates contain ':'; IPv4 uses '.'. Strip any zone-id
        // suffix ("fe80::1%eth0") before classifying.
        if (ip.Contains(':'))
        {
            var addr = ip.Split('%')[0];
            // Loopback ::1 (and the all-zeros unspecified address).
            if (addr == "::1" || addr == "::") return true;
            // Link-local fe80::/10 — fe80..febf.
            if (addr.StartsWith("fe8", StringComparison.OrdinalIgnoreCase)
                || addr.StartsWith("fe9", StringComparison.OrdinalIgnoreCase)
                || addr.StartsWith("fea", StringComparison.OrdinalIgnoreCase)
                || addr.StartsWith("feb", StringComparison.OrdinalIgnoreCase))
                return true;
            // Unique-local fc00::/7 — first byte fc or fd.
            if (addr.StartsWith("fc", StringComparison.OrdinalIgnoreCase)
                || addr.StartsWith("fd", StringComparison.OrdinalIgnoreCase))
                return true;
            // Any other IPv6 address is global/routable → a real leak.
            return false;
        }

        // IPv4 ranges.
        if (ip.StartsWith("127.")) return true;
        if (ip.StartsWith("169.254.")) return true;
        if (ip.StartsWith("10.")) return true;
        if (ip.StartsWith("192.168.")) return true;
        if (ip.StartsWith("172."))
        {
            var parts = ip.Split('.');
            if (parts.Length > 1
                && int.TryParse(parts[1], out var second)
                && second is >= 16 and <= 31)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Extract a nested dictionary from a parent dictionary. Returns an empty
    /// dict if the key is missing or not a dictionary.
    /// </summary>
    private static Dictionary<string, object?> ExtractDict(
        Dictionary<string, object?>? parent, string key)
    {
        if (parent is null || !parent.ContainsKey(key))
            return new();

        var val = parent[key];
        if (val is JsonElement je)
        {
            // If it's a JsonElement, try to deserialise it
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object?>>(je.GetRawText())
                    ?? new();
            }
            catch
            {
                return new();
            }
        }

        if (val is Dictionary<string, object?> dict)
            return dict;

        return new();
    }

    /// <summary>
    /// Safely get a value from a dictionary, handling string and
    /// double/int/bool types. Returns null if the key is missing,
    /// the value is null, or unpacking fails.
    /// </summary>
    private static object? SafeGet(Dictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.ContainsKey(key))
            return null;

        var val = dict[key];
        if (val is null)
            return null;

        if (val is JsonElement je)
        {
            try
            {
                return je.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => je.GetString(),
                    System.Text.Json.JsonValueKind.Number =>
                        je.TryGetDouble(out var d) ? d : (object?)null,
                    System.Text.Json.JsonValueKind.True or
                    System.Text.Json.JsonValueKind.False => je.GetBoolean(),
                    _ => null,
                };
            }
            catch
            {
                return null;
            }
        }

        return val;
    }

    /// <summary>
    /// Safely extract a numeric value from a payload dictionary, handling
    /// the type-zoo that cross-language serialisation produces. The
    /// previous code did <c>SafeGet(dict, key) as double?</c> which
    /// returns null when the boxed value is <c>int</c> (DeviceTemplateBuilder
    /// stores hardware_concurrency / screen.width / etc as int) or
    /// <c>long</c> (Dapper rebinds SQLite INTEGER to long). Same trap
    /// for JsonElement of kind Number — its underlying CLR type is
    /// undefined, you have to call TryGet*. This helper covers all
    /// shapes and returns null only when the value is genuinely
    /// missing or non-numeric.
    /// </summary>
    private static double? SafeGetNumber(Dictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.ContainsKey(key)) return null;
        var val = dict[key];
        return val switch
        {
            null            => null,
            int i           => i,
            long l          => l,
            double d        => d,
            float f         => f,
            decimal m       => (double)m,
            string s when double.TryParse(s,
                                          System.Globalization.NumberStyles.Any,
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          out var parsed) => parsed,
            JsonElement je when je.ValueKind == JsonValueKind.Number
                                && je.TryGetDouble(out var jd)  => jd,
            JsonElement je when je.ValueKind == JsonValueKind.String
                                && double.TryParse(je.GetString(),
                                          System.Globalization.NumberStyles.Any,
                                          System.Globalization.CultureInfo.InvariantCulture,
                                          out var sp) => sp,
            _               => null,
        };
    }

    /// <summary>
    /// Two IANA timezone IDs that refer to the SAME physical zone (e.g.
    /// "Europe/Kiev" and "Europe/Kyiv"). Chrome's V8 + ICU use the
    /// legacy "Europe/Kiev" name in Intl.DateTimeFormat output even
    /// when the system / payload uses the modern "Europe/Kyiv" — so
    /// the self-check would always FAIL on this comparison. Treat
    /// known aliases as equivalent. Add new entries as we encounter
    /// them (full IANA aliases list is ~80 entries; we ship the few
    /// our users actually hit).
    /// </summary>
    private static readonly Dictionary<string, string> _tzAliases =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["Europe/Kyiv"]         = "Europe/Kiev",
        ["Europe/Kiev"]         = "Europe/Kiev",
        ["Asia/Kolkata"]        = "Asia/Calcutta",
        ["Asia/Calcutta"]       = "Asia/Calcutta",
        ["Asia/Ho_Chi_Minh"]    = "Asia/Saigon",
        ["Asia/Saigon"]         = "Asia/Saigon",
        ["America/Indiana/Indianapolis"] = "America/Indianapolis",
        ["America/Indianapolis"] = "America/Indianapolis",
    };

    /// <summary>
    /// Coerce ANY array-shaped value (JsonElement.Array, IEnumerable,
    /// string[], List&lt;object&gt;, ReadOnlyCollection&lt;object&gt;,
    /// arrays-of-arrays etc) into a single comma-separated string.
    /// Returns null when the input is null or genuinely not an array.
    /// Used by navigator.languages where the C# payload stores a
    /// List&lt;string&gt; but Selenium hands back a ReadOnlyCollection
    /// of objects — the previous typed cast `as List&lt;object&gt;`
    /// failed for the latter and produced empty Expected / Actual.
    /// </summary>
    private static string? ToCsv(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;  // already CSV?
        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Array)
                return string.Join(",",
                    je.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String
                        ? (e.GetString() ?? "") : e.GetRawText()));
            if (je.ValueKind == JsonValueKind.String)
                return je.GetString();
            return null;
        }
        if (value is System.Collections.IEnumerable arr)
            return string.Join(",", arr.Cast<object?>().Select(o => o?.ToString() ?? ""));
        return value.ToString();
    }

    /// <summary>
    /// audit SELFCHECK-04: count the elements of an array-shaped value,
    /// handling the shapes DeviceTemplateBuilder / System.Text.Json /
    /// Selenium can hand us (List&lt;object&gt;, JsonElement.Array, any
    /// IEnumerable). Returns 0 for null or non-array values. A plain
    /// string is NOT treated as an enumerable here (we don't want its
    /// char count) — only genuine collections are counted.
    /// </summary>
    private static int CountList(object? value)
    {
        switch (value)
        {
            case null:
                return 0;
            case JsonElement je:
                return je.ValueKind == JsonValueKind.Array ? je.GetArrayLength() : 0;
            case string:
                return 0;
            case System.Collections.ICollection coll:
                return coll.Count;
            case System.Collections.IEnumerable en:
                return en.Cast<object?>().Count();
            default:
                return 0;
        }
    }

    private static bool TimezoneMatch(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) return true;
        var canonExpected = _tzAliases.TryGetValue(expected, out var ce) ? ce : expected;
        var canonActual   = _tzAliases.TryGetValue(actual,   out var ca) ? ca : actual;
        return string.Equals(canonExpected, canonActual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Safely get a typed value, casting the result. Useful for T? values.
    /// </summary>
    private static T? SafeGetAs<T>(Dictionary<string, object?>? dict, string key)
        where T : class
    {
        if (dict is null || !dict.ContainsKey(key))
            return null;

        var val = dict[key];
        if (val is null)
            return null;

        if (val is JsonElement je)
        {
            try
            {
                if (typeof(T) == typeof(string))
                    return (T?)(object?)je.GetString();
                // For complex types, try full deserialisation
                return JsonSerializer.Deserialize<T>(je.GetRawText());
            }
            catch
            {
                return null;
            }
        }

        return val as T;
    }

    /// <summary>
    /// Check if two string values match (case-sensitive, null-safe).
    /// </summary>
    private static bool StringMatch(string? expected, string? actual)
    {
        if (expected is null && actual is null) return true;
        if (expected is null || actual is null) return false;
        return expected.Equals(actual, StringComparison.Ordinal);
    }

    /// <summary>
    /// Check if two numeric values match (handles nulls and conversion).
    /// </summary>
    private static bool NumericMatch(double? expected, double? actual)
    {
        if (expected is null && actual is null) return true;
        if (expected is null || actual is null) return false;
        return Math.Abs(expected.Value - actual.Value) < 0.01;
    }

    /// <summary>
    /// Check if two float values match within a tolerance.
    /// </summary>
    private static bool FloatMatchWithTolerance(double? expected, double? actual, double tolerance)
    {
        if (expected is null && actual is null) return true;
        if (expected is null || actual is null) return false;
        return Math.Abs(expected.Value - actual.Value) <= tolerance;
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Diagnostics;

/// <summary>
/// Phase-2 stub. Returns a deterministic "test result" derived from
/// the proxy's URL hash so the UI can demonstrate the Test-all flow,
/// the latency column, and the IP-type colouring without actually
/// going to the network.
///
/// Phase 3 replaces this with a real <see cref="HttpClient"/>-based
/// probe (resolves through the proxy, hits ip-api.com, measures TTFB).
/// </summary>
public sealed class StubProxyTester : IProxyTester
{
    private readonly ILogger<StubProxyTester> _log;

    public StubProxyTester(ILogger<StubProxyTester> log) => _log = log;

    public async Task<ProxyTestResult> TestAsync(Proxy proxy, CancellationToken ct = default)
    {
        // Yield so callers awaiting many tests sequentially see the
        // UI tick between rows. 200-500ms is realistic for a real probe.
        var seed = StableHash(proxy.Url);
        var rnd  = new Random(seed);
        var fakeLatency = 80 + rnd.Next(0, 400);

        await Task.Delay(150 + rnd.Next(0, 200), ct);

        // Deterministic-by-URL "geo" so each proxy tile looks consistent
        // across reloads. Real geo will replace this in Phase 3.
        var ipType = (seed & 3) switch
        {
            0 => IpType.Datacenter,
            1 => IpType.Residential,
            2 => IpType.Mobile,
            _ => IpType.Datacenter,
        };

        var samples = new (string Country, string Code, string City)[]
        {
            ("United States",  "US", "New York"),
            ("Ukraine",        "UA", "Kyiv"),
            ("Germany",        "DE", "Berlin"),
            ("United Kingdom", "GB", "London"),
            ("Brazil",         "BR", "São Paulo"),
            ("Japan",          "JP", "Tokyo"),
        };
        var pick = samples[Math.Abs(seed) % samples.Length];

        var result = new ProxyTestResult
        {
            Ok          = true,
            Ip          = SyntheticIp(seed),
            Country     = pick.Country,
            CountryCode = pick.Code,
            City        = pick.City,
            Asn         = $"AS{10000 + (seed & 0x3FFF)}",
            Isp         = ipType == IpType.Datacenter ? "DigitalOcean (stub)" : "Comcast (stub)",
            IpType      = ipType,
            LatencyMs   = fakeLatency,
            At          = DateTime.UtcNow,
        };
        _log.LogInformation(
            "[stub] Tested proxy '{Slug}' → {Country}, {IpType}, {Latency}ms",
            proxy.Slug, result.Country, result.IpType, result.LatencyMs);
        return result;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 23;
            foreach (var c in s) h = h * 31 + c;
            return h;
        }
    }

    private static string SyntheticIp(int seed)
    {
        // Stable but obviously-fake IP: 10.x.y.z so it's clearly stub.
        var b = BitConverter.GetBytes(seed);
        return $"10.{b[0]}.{b[1]}.{b[2]}";
    }
}

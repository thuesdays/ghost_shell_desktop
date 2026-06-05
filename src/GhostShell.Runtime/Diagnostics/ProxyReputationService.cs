// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Diagnostics;

/// <summary>
/// Deterministic IP-reputation scorer. Turns the proxy diagnostics we
/// already collect (ip-api <see cref="IpType"/>, ISP/ASN strings,
/// <see cref="ProxyHealth"/>) into a 0–100 "burned-ness" score and a
/// <see cref="ReputationBand"/>, optionally blended with an external
/// fraud-score provider.
///
/// Why heuristic-first: ip-api already tells us datacenter vs residential
/// vs mobile and flags known-proxy/VPN ASNs. That alone catches the
/// dominant failure mode (a datacenter IP getting Google-walled). The
/// external provider is a precision booster, not a dependency.
/// </summary>
public sealed class ProxyReputationService : IProxyReputationService
{
    private readonly IIpReputationProvider _external;
    private readonly ILogger<ProxyReputationService> _log;

    public ProxyReputationService(
        IIpReputationProvider external,
        ILogger<ProxyReputationService> log)
    {
        _external = external;
        _log      = log;
    }

    // Base score per ip-type. Residential/mobile are what real users have;
    // datacenter is the "burned" default; unknown sits in the middle so an
    // un-probed proxy reads as Suspect, not falsely Clean.
    private const int BaseResidential = 10;
    private const int BaseMobile      = 5;
    private const int BaseDatacenter  = 60;
    private const int BaseUnknown     = 30;

    // Substrings (lower-cased) in ISP/ASN that mark hosting / VPN / proxy
    // networks — the providers Google & Cloudflare weight most heavily.
    // Deliberately broad; a false-positive only nudges a proxy toward
    // "Suspect", it never hard-blocks on its own.
    private static readonly string[] HostingKeywords =
    {
        "hosting", "datacenter", "data center", "data-center", "colo", "colocation",
        "server", "cloud", "vps", "dedicated", "vpn", "proxy",
        // well-known hosting/VPN ASNs and brands
        "datacamp", "cdn77", "ovh", "hetzner", "digitalocean", "linode", "vultr",
        "leaseweb", "m247", "choopa", "contabo", "scaleway", "amazon", "aws",
        "google llc", "google cloud", "microsoft", "azure", "oracle", "alibaba",
        "tencent", "ovhcloud", "packet", "quadranet", "hostwinds", "ipxo",
        "nforce", "worldstream", "serverius", "g-core", "gcore", "fastly",
    };

    public ProxyReputationReport Evaluate(Proxy proxy)
        => Score(proxy, external: null);

    // Per-IP cache of external fraud-score results. An IP's reputation barely
    // moves hour to hour, so we avoid re-hitting the provider (and paying the
    // HTTP latency) for the same IP within the TTL.
    private static readonly TimeSpan ExternalTtl = TimeSpan.FromHours(6);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, ExternalReputation? Rep)> _extCache = new();

    public async Task<ProxyReputationReport> EvaluateAsync(
        Proxy proxy, CancellationToken ct = default)
    {
        ExternalReputation? ext = null;
        var ip = proxy.LastIp;
        if (_external.IsEnabled && !string.IsNullOrWhiteSpace(ip))
        {
            if (_extCache.TryGetValue(ip!, out var cached) && DateTime.UtcNow - cached.At < ExternalTtl)
            {
                ext = cached.Rep;
            }
            else
            {
                try
                {
                    ext = await _external.LookupAsync(ip!, ct);
                    _extCache[ip!] = (DateTime.UtcNow, ext);
                }
                catch (Exception ex)
                {
                    // Fail-open: a flaky provider must never block a launch or
                    // skew the score. Heuristic-only is a safe fallback.
                    _log.LogDebug(ex,
                        "Proxy reputation: external provider lookup failed for {Ip}; using heuristic only",
                        ip);
                }
            }
        }
        return Score(proxy, ext);
    }

    private static ProxyReputationReport Score(Proxy proxy, ExternalReputation? external)
    {
        var reasons = new List<string>();

        var score = proxy.IpType switch
        {
            IpType.Residential => BaseResidential,
            IpType.Mobile      => BaseMobile,
            IpType.Datacenter  => BaseDatacenter,
            _                  => BaseUnknown,
        };
        reasons.Add(proxy.IpType switch
        {
            IpType.Residential => "residential IP (real-user network)",
            IpType.Mobile      => "mobile IP (carrier network — best reputation)",
            IpType.Datacenter  => "datacenter IP (heavily penalised by Google/Cloudflare)",
            _                  => "IP type unknown (not probed) — treat with caution",
        });

        // Hosting/VPN keyword in ISP or ASN string.
        var haystack = ((proxy.Isp ?? "") + " " + (proxy.Asn ?? "")).ToLowerInvariant();
        var hit = HostingKeywords.FirstOrDefault(k => haystack.Contains(k, StringComparison.Ordinal));
        if (hit is not null)
        {
            score += 25;
            reasons.Add($"ISP/ASN matches hosting/VPN marker '{hit}'");
        }

        // Health from the last probe (Google reachability etc.).
        switch (proxy.Health)
        {
            case ProxyHealth.Critical:
                score += 20;
                reasons.Add("last health check was Critical");
                break;
            case ProxyHealth.Warning:
                score += 10;
                reasons.Add("last health check was Warning");
                break;
        }

        // External fraud score — take the worst of heuristic vs provider so a
        // provider can only ESCALATE, never falsely clean a bad IP.
        if (external is not null)
        {
            if (external.FraudScore > score)
            {
                reasons.Add($"{external.Source ?? "external"} fraud score {external.FraudScore}");
                score = external.FraudScore;
            }
            else
            {
                reasons.Add($"{external.Source ?? "external"} fraud score {external.FraudScore} (≤ heuristic)");
            }
            if (external.IsVpn)   { score += 10; reasons.Add($"{external.Source ?? "external"} flags VPN"); }
            if (external.IsProxy) { score += 10; reasons.Add($"{external.Source ?? "external"} flags proxy"); }
        }

        score = Math.Clamp(score, 0, 100);
        var band = score >= 65 ? ReputationBand.Burned
                 : score >= 30 ? ReputationBand.Suspect
                 : ReputationBand.Clean;

        var rec = band switch
        {
            ReputationBand.Clean   => "Good for Google/sensitive targets.",
            ReputationBand.Suspect => "Usable, but expect occasional challenges; prefer residential/mobile for Google.",
            ReputationBand.Burned  => "Avoid for Google/ad work — use a residential or mobile proxy.",
        };

        return new ProxyReputationReport(score, band, proxy.IpType, reasons, rec);
    }
}

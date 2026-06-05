// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Runtime.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// `Proxy` resolves to the GhostShell.Tests.Proxy namespace under our using
// set, not the model type — alias it (same pattern as CaptchaRecoveryServiceTests).
using ProxyModel = GhostShell.Core.Models.Proxy;

namespace GhostShell.Tests.Diagnostics;

/// <summary>
/// Feature #1 — proxy IP-reputation scoring. Pins the band boundaries and
/// the external-provider blend so the launch gate's decisions stay stable.
/// </summary>
public sealed class ProxyReputationServiceTests
{
    private static ProxyReputationService Svc(IIpReputationProvider? ext = null)
        => new(ext ?? new NullIpReputationProvider(),
               NullLogger<ProxyReputationService>.Instance);

    private static ProxyModel MakeProxy(
        IpType ipType, string? isp = null, string? asn = null,
        ProxyHealth health = ProxyHealth.Unknown, string? ip = "1.2.3.4")
        => new()
        {
            Slug = "p1",
            Url  = "http://1.2.3.4:8080",
            IpType = ipType,
            Isp = isp,
            Asn = asn,
            Health = health,
            LastIp = ip,
        };

    [Fact]
    public void Residential_IsClean()
    {
        var r = Svc().Evaluate(MakeProxy(IpType.Residential, isp: "Comcast Cable"));
        Assert.Equal(ReputationBand.Clean, r.Band);
        Assert.True(r.Score < 30);
    }

    [Fact]
    public void Mobile_IsClean_AndLowestScore()
    {
        var mobile = Svc().Evaluate(MakeProxy(IpType.Mobile, isp: "Vodafone"));
        var resi   = Svc().Evaluate(MakeProxy(IpType.Residential, isp: "Comcast"));
        Assert.Equal(ReputationBand.Clean, mobile.Band);
        Assert.True(mobile.Score <= resi.Score);
    }

    [Fact]
    public void Datacenter_PlusHostingIsp_IsBurned()
    {
        // The exact case that triggered the Google block: Datacamp datacenter.
        var r = Svc().Evaluate(MakeProxy(IpType.Datacenter, isp: "Datacamp Limited", asn: "AS212238 Datacamp Limited"));
        Assert.Equal(ReputationBand.Burned, r.Band);
        Assert.True(r.Score >= 65, $"expected Burned score, got {r.Score}");
        Assert.Contains(r.Reasons, x => x.Contains("datacamp", System.StringComparison.OrdinalIgnoreCase)
                                     || x.Contains("hosting", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Datacenter_NoKeyword_IsSuspect()
    {
        // Datacenter base (60) alone lands in Suspect, not Burned.
        var r = Svc().Evaluate(MakeProxy(IpType.Datacenter, isp: "Some ISP", asn: "AS1 Some ISP"));
        Assert.Equal(ReputationBand.Suspect, r.Band);
    }

    [Fact]
    public void Unknown_IsSuspect_NotFalselyClean()
    {
        var r = Svc().Evaluate(MakeProxy(IpType.Unknown));
        Assert.Equal(ReputationBand.Suspect, r.Band);
    }

    [Fact]
    public void CriticalHealth_RaisesScore()
    {
        var ok  = Svc().Evaluate(MakeProxy(IpType.Datacenter, isp: "x", health: ProxyHealth.Unknown));
        var bad = Svc().Evaluate(MakeProxy(IpType.Datacenter, isp: "x", health: ProxyHealth.Critical));
        Assert.True(bad.Score > ok.Score);
    }

    [Fact]
    public async Task ExternalProvider_CanEscalate_ButNeverCleans()
    {
        // A clean residential heuristic + a high external fraud score → escalates.
        var high = new FakeProvider(new ExternalReputation(95, IsProxy: true, IsVpn: true, "test"));
        var r = await Svc(high).EvaluateAsync(MakeProxy(IpType.Residential, isp: "Comcast"));
        Assert.Equal(ReputationBand.Burned, r.Band);

        // A burned heuristic + a LOW external score → stays burned (worst-of).
        var low = new FakeProvider(new ExternalReputation(0, IsProxy: false, IsVpn: false, "test"));
        var r2 = await Svc(low).EvaluateAsync(MakeProxy(IpType.Datacenter, isp: "Datacamp"));
        Assert.Equal(ReputationBand.Burned, r2.Band);
    }

    [Fact]
    public async Task NullProvider_FallsBackToHeuristic()
    {
        var r = await Svc().EvaluateAsync(MakeProxy(IpType.Datacenter, isp: "Datacamp"));
        Assert.Equal(ReputationBand.Burned, r.Band);
    }

    [Fact]
    public void Recommendation_IsPopulated()
    {
        var r = Svc().Evaluate(MakeProxy(IpType.Datacenter, isp: "Datacamp"));
        Assert.False(string.IsNullOrWhiteSpace(r.Recommendation));
        Assert.NotEmpty(r.Reasons);
    }

    private sealed class FakeProvider : IIpReputationProvider
    {
        private readonly ExternalReputation _r;
        public FakeProvider(ExternalReputation r) => _r = r;
        public bool IsEnabled => true;
        public Task<ExternalReputation?> LookupAsync(string ip, CancellationToken ct = default)
            => Task.FromResult<ExternalReputation?>(_r);
    }
}

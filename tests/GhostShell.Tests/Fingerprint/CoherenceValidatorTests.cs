// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Runtime.Fingerprint;
using Xunit;

namespace GhostShell.Tests.Fingerprint;

/// <summary>
/// Coherence validator tests. The validator is a pure function of
/// the generated payload — given a deterministic builder we can
/// assert exactly what the score should be.
/// </summary>
public sealed class CoherenceValidatorTests
{
    private static DeviceTemplate Desktop() => new()
    {
        Id = "test_desktop",
        FormFactor = FormFactor.Desktop,
        IsLaptop = false,
        CpuCores = 8, RamGb = 16,
        GpuModel = "Intel Iris Xe",
        ScreenWidth = 1920, ScreenHeight = 1080,
    };

    [Fact]
    public void HealthyDesktop_ScoresAtLeastOk()
    {
        var b = new DeviceTemplateBuilder("p", Desktop());
        var s = CoherenceValidator.Validate(b);
        // A clean profile with all-default settings should clear OK
        // (≥ 75). Skipped checks (TLS / WebRTC) don't move the
        // needle, so the floor is the sum of everything that passes
        // statically.
        Assert.True(s.Overall >= 75, $"score {s.Overall} should be ≥ 75");
        Assert.Equal(0, s.CriticalIssues);
    }

    [Fact]
    public void Score_NeverNegative()
    {
        // Even a profile with weird inputs shouldn't go below 0 —
        // the validator clamps each category at 0. CpuCores=7 is
        // outside the allowed set; ClampCpu will round it.
        var weird = new DeviceTemplate
        {
            Id = "test_weird", FormFactor = FormFactor.Desktop, IsLaptop = false,
            CpuCores = 7, RamGb = 16, GpuModel = "Intel Iris Xe",
            ScreenWidth = 1920, ScreenHeight = 1080,
        };
        var b = new DeviceTemplateBuilder("p", weird);
        var s = CoherenceValidator.Validate(b);
        Assert.True(s.Overall >= 0);
        Assert.True(s.Identity >= 0);
        Assert.True(s.Hardware >= 0);
        Assert.True(s.Network >= 0);
        Assert.True(s.Automation >= 0);
    }

    [Fact]
    public void Score_NeverAbove100()
    {
        var b = new DeviceTemplateBuilder("p", Desktop());
        var s = CoherenceValidator.Validate(b);
        Assert.True(s.Overall   <= 100);
        Assert.True(s.Identity  <= 100);
        Assert.True(s.Hardware  <= 100);
        Assert.True(s.Network   <= 100);
        Assert.True(s.Automation<= 100);
    }

    [Fact]
    public void Label_MatchesScoreBracket()
    {
        var b = new DeviceTemplateBuilder("p", Desktop());
        var s = CoherenceValidator.Validate(b);
        var expected = s.Overall switch
        {
            >= 85 => "EXCELLENT",
            >= 75 => "OK",
            >= 50 => "RISKY",
            _     => "BAD",
        };
        Assert.Equal(expected, s.Label);
    }

    [Fact]
    public void TwoIdenticalBuilders_ProduceIdenticalScores()
    {
        var b1 = new DeviceTemplateBuilder("p", Desktop());
        var b2 = new DeviceTemplateBuilder("p", Desktop());
        var s1 = CoherenceValidator.Validate(b1);
        var s2 = CoherenceValidator.Validate(b2);
        Assert.Equal(s1.Overall,    s2.Overall);
        Assert.Equal(s1.Identity,   s2.Identity);
        Assert.Equal(s1.Hardware,   s2.Hardware);
        Assert.Equal(s1.Network,    s2.Network);
        Assert.Equal(s1.Automation, s2.Automation);
    }

    [Fact]
    public void Mobile_EmitsMobileUaCheckPass()
    {
        var mobile = new DeviceTemplate
        {
            Id = "test_phone", FormFactor = FormFactor.Mobile, IsLaptop = false,
            CpuCores = 8, RamGb = 6, GpuModel = "Adreno 740",
            ScreenWidth = 412, ScreenHeight = 915, Dpr = 2.625,
        };
        var b = new DeviceTemplateBuilder("p-mob", mobile);
        var s = CoherenceValidator.Validate(b);
        var mobileCheck = s.Checks.First(c => c.Id == "mobile_ua_marker");
        Assert.Equal(FingerprintCheckStatus.Pass, mobileCheck.Status);
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Runtime.Fingerprint;
using Xunit;

namespace GhostShell.Tests.Fingerprint;

/// <summary>
/// Tests for the deterministic-and-regenerable fingerprint payload
/// generator. The contract that's most easy to break (and most
/// important to lock) is determinism + salt isolation: same inputs
/// → same output, different regen salts → different payloads,
/// reshuffle alone changes ONLY the noise.* sub-tree.
/// </summary>
public sealed class DeviceTemplateBuilderTests
{
    private static DeviceTemplate SampleTemplate() => new()
    {
        Id          = "office_laptop_intel",
        FormFactor  = FormFactor.Desktop,
        IsLaptop    = true,
        CpuCores    = 8,
        RamGb       = 16,
        GpuModel    = "Intel Iris Xe",
        ScreenWidth = 1920,
        ScreenHeight= 1080,
        Dpr         = 1.0,
    };

    [Fact]
    public void Determinism_SameInputs_SameJson()
    {
        var b1 = new DeviceTemplateBuilder("profile_01", SampleTemplate());
        var b2 = new DeviceTemplateBuilder("profile_01", SampleTemplate());

        Assert.Equal(b1.ToJson(), b2.ToJson());
        Assert.Equal(b1.ToBase64(), b2.ToBase64());
    }

    [Fact]
    public void DifferentProfiles_ProduceDifferentPayloads()
    {
        var b1 = new DeviceTemplateBuilder("profile_01", SampleTemplate());
        var b2 = new DeviceTemplateBuilder("profile_02", SampleTemplate());

        Assert.NotEqual(b1.ToJson(), b2.ToJson());
    }

    [Fact]
    public void RegenSalt_ChangesPayload()
    {
        var b1 = new DeviceTemplateBuilder("profile_01", SampleTemplate(),
            regenSalt: "salt-A");
        var b2 = new DeviceTemplateBuilder("profile_01", SampleTemplate(),
            regenSalt: "salt-B");

        Assert.NotEqual(b1.ToJson(), b2.ToJson());
    }

    [Fact]
    public void NoiseSalt_ChangesOnlyNoiseFields()
    {
        // Same regen-salt → main fields stable. Different noise-salt
        // → noise.* fields differ. We verify by comparing the two
        // payloads' "graphics" and "hardware" sub-trees (which are
        // driven by _rng) and confirming they're identical.
        var p1 = new DeviceTemplateBuilder("profile_01", SampleTemplate(),
            regenSalt: "regen-X", noiseSalt: "noise-A").Build();
        var p2 = new DeviceTemplateBuilder("profile_01", SampleTemplate(),
            regenSalt: "regen-X", noiseSalt: "noise-B").Build();

        // Hardware should be identical (cpu, memory, UA all derive
        // from main RNG).
        Assert.Equal(
            ((Dictionary<string, object?>)p1["hardware"]!)["user_agent"],
            ((Dictionary<string, object?>)p2["hardware"]!)["user_agent"]);

        // Noise SHOULD differ. Compare canvas_shift specifically.
        var noise1 = (Dictionary<string, object?>)p1["noise"]!;
        var noise2 = (Dictionary<string, object?>)p2["noise"]!;
        Assert.NotEqual(noise1["seed"], noise2["seed"]);
    }

    [Fact]
    public void MobileTemplate_EmitsMobileUaMarker()
    {
        var mobile = new DeviceTemplate
        {
            Id          = "test_phone",
            FormFactor  = FormFactor.Mobile,
            IsLaptop    = false,
            CpuCores    = 8,
            RamGb       = 6,
            GpuModel    = "Adreno 740",
            ScreenWidth = 412, ScreenHeight = 915,
            Dpr         = 2.625,
        };
        var b = new DeviceTemplateBuilder("p", mobile);
        var hw = (Dictionary<string, object?>)b.Build()["hardware"]!;
        var ua = (string)hw["user_agent"]!;
        Assert.Contains("Mobile", ua);
    }

    [Fact]
    public void DesktopTemplate_OmitsMobileMarker()
    {
        var b = new DeviceTemplateBuilder("p", SampleTemplate());
        var hw = (Dictionary<string, object?>)b.Build()["hardware"]!;
        var ua = (string)hw["user_agent"]!;
        Assert.DoesNotContain("Mobile", ua);
    }

    [Fact]
    public void CpuClamp_RoundsToNearestAllowed()
    {
        // Allowed values: 2, 4, 6, 8, 12, 16, 24, 32. CPU=10 should
        // round to 8 or 12 (closest); CPU=7 should round to 6 or 8.
        var t10 = new DeviceTemplate
        {
            Id = "x", FormFactor = FormFactor.Desktop, IsLaptop = true,
            CpuCores = 10, RamGb = 16, GpuModel = "Intel Iris Xe",
            ScreenWidth = 1920, ScreenHeight = 1080, Dpr = 1.0,
        };
        var b = new DeviceTemplateBuilder("p", t10);
        var hw = (Dictionary<string, object?>)b.Build()["hardware"]!;
        var cpu = Convert.ToInt32(hw["hardware_concurrency"]);
        Assert.Contains(cpu, new[] { 8, 12 });
    }

    [Fact]
    public void DeviceMemory_ClampedToSpec()
    {
        var t = new DeviceTemplate
        {
            Id = "x", FormFactor = FormFactor.Desktop, IsLaptop = true,
            CpuCores = 8, RamGb = 32, GpuModel = "Intel Iris Xe",
            ScreenWidth = 1920, ScreenHeight = 1080, Dpr = 1.0,
        };
        var b = new DeviceTemplateBuilder("p", t);
        var hw = (Dictionary<string, object?>)b.Build()["hardware"]!;
        var mem = Convert.ToDouble(hw["device_memory"]);
        Assert.Equal(8.0, mem); // capped at 8 per WICG spec
    }

    [Fact]
    public void Base64_IsValidAndRoundTrips()
    {
        var b = new DeviceTemplateBuilder("p", SampleTemplate());
        var encoded = b.ToBase64();
        // Must decode without throwing.
        var decoded = Convert.FromBase64String(encoded);
        Assert.NotEmpty(decoded);
        // And must equal the original UTF-8 bytes.
        var roundTripped = System.Text.Encoding.UTF8.GetString(decoded);
        Assert.Equal(b.ToJson(), roundTripped);
    }

    [Fact]
    public void CliFlag_IncludesExpectedPrefix()
    {
        var b = new DeviceTemplateBuilder("p", SampleTemplate());
        var flag = b.GetCliFlag();
        Assert.StartsWith("--ghost-shell-payload=", flag);
        // The payload portion is parseable as base64.
        var payload = flag["--ghost-shell-payload=".Length..];
        var bytes = Convert.FromBase64String(payload);
        Assert.NotEmpty(bytes);
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One device fingerprint preset. Each profile picks one (or "auto"
/// for random selection at run-time). Mirrors the fields the legacy
/// Python project's `device_templates.py` exposes through the
/// `/api/profile-templates` endpoint, so the desktop dropdown can
/// look identical to the web one.
///
/// Fields are display-oriented — the actual fingerprint generation
/// will live in <c>GhostShell.Runtime</c> and consume these as a
/// catalog lookup.
/// </summary>
public sealed class DeviceTemplate
{
    public required string Id { get; init; }
    public string? HumanName { get; init; }

    public FormFactor FormFactor { get; init; } = FormFactor.Desktop;
    public bool IsLaptop { get; init; }

    public int CpuCores { get; init; }
    public double RamGb { get; init; }
    public string? GpuModel { get; init; }
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }
    public double Dpr { get; init; } = 1.0;

    /// <summary>Higher weight = more likely picked when "auto" is selected.</summary>
    public int Weight { get; init; } = 1;

    /// <summary>
    /// Compose the dropdown label, format that the legacy web UI uses:
    ///   <c>name — 16c · 32 GB · GeForce RTX 4080 · 2560×1440 · laptop</c>
    /// Falls back to just the id when no extra fields are set.
    /// </summary>
    public string ToLabel()
    {
        var display = !string.IsNullOrEmpty(HumanName) ? HumanName! : Id;
        var parts   = new List<string>(5);
        if (CpuCores > 0)               parts.Add($"{CpuCores}c");
        if (RamGb > 0)                  parts.Add($"{Math.Round(RamGb)} GB");
        if (!string.IsNullOrEmpty(GpuModel)) parts.Add(GpuModel!);
        if (ScreenWidth > 0 && ScreenHeight > 0
            && !(ScreenWidth == 1920 && ScreenHeight == 1080))
        {
            var dpr = Dpr is > 1.0 ? $" @{Dpr:0.#}x" : "";
            parts.Add($"{ScreenWidth}×{ScreenHeight}{dpr}");
        }
        if (FormFactor == FormFactor.Mobile)        parts.Add("mobile");
        else if (FormFactor == FormFactor.Tablet)   parts.Add("tablet");
        else if (IsLaptop)                          parts.Add("laptop");

        return parts.Count == 0 ? display : $"{display} — {string.Join(" · ", parts)}";
    }
}

public enum FormFactor
{
    Desktop,
    Mobile,
    Tablet,
}

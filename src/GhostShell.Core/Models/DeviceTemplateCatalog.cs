// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Built-in catalog of device templates. Curated subset of the
/// legacy <c>device_templates.py</c> — the same entries the web
/// dropdown showed (Desktop / Laptop section + Mobile + Tablet).
///
/// This is intentionally a static read-only list. When the runtime
/// (Phase 3) gets ported it'll consume these directly. For now the
/// catalog drives just the Profile-editor dropdown.
///
/// Adding a new device = one entry below; the dropdown picks it up
/// automatically.
/// </summary>
public static class DeviceTemplateCatalog
{
    public static IReadOnlyList<DeviceTemplate> All { get; } = new[]
    {
        // ─── Desktop ───────────────────────────────────────────
        new DeviceTemplate {
            Id = "office_desktop_intel",
            CpuCores = 8, RamGb = 8, GpuModel = "UHD Graphics 770",
            ScreenWidth = 1920, ScreenHeight = 1080, Weight = 5,
        },
        new DeviceTemplate {
            Id = "office_laptop_intel", IsLaptop = true,
            CpuCores = 8, RamGb = 16, GpuModel = "Iris(R) Xe Graphics",
            ScreenWidth = 1920, ScreenHeight = 1080, Weight = 6,
        },
        new DeviceTemplate {
            Id = "gaming_nvidia_mid",
            CpuCores = 12, RamGb = 16, GpuModel = "GeForce RTX 4060",
            ScreenWidth = 1920, ScreenHeight = 1080, Weight = 3,
        },
        new DeviceTemplate {
            Id = "gaming_nvidia_high",
            CpuCores = 16, RamGb = 32, GpuModel = "GeForce RTX 4070",
            ScreenWidth = 2560, ScreenHeight = 1440, Weight = 2,
        },
        new DeviceTemplate {
            Id = "amd_desktop_mid",
            CpuCores = 12, RamGb = 16, GpuModel = "Radeon RX 6600 XT",
            ScreenWidth = 1920, ScreenHeight = 1080, Weight = 2,
        },
        new DeviceTemplate {
            Id = "budget_laptop", IsLaptop = true,
            CpuCores = 4, RamGb = 8, GpuModel = "UHD Graphics",
            ScreenWidth = 1366, ScreenHeight = 768, Weight = 3,
        },
        new DeviceTemplate {
            Id = "gaming_nvidia_4070_super",
            CpuCores = 20, RamGb = 32, GpuModel = "GeForce RTX 4070 SUPER",
            ScreenWidth = 2560, ScreenHeight = 1440, Weight = 3,
        },
        new DeviceTemplate {
            Id = "gaming_nvidia_4080_super",
            CpuCores = 24, RamGb = 64, GpuModel = "GeForce RTX 4080 SUPER",
            ScreenWidth = 2560, ScreenHeight = 1440, Weight = 2,
        },
        new DeviceTemplate {
            Id = "enthusiast_nvidia_4090_4k",
            CpuCores = 32, RamGb = 64, GpuModel = "GeForce RTX 4090",
            ScreenWidth = 3840, ScreenHeight = 2160, Dpr = 1.5, Weight = 1,
        },
        new DeviceTemplate {
            Id = "workstation_threadripper_a4000",
            CpuCores = 48, RamGb = 128, GpuModel = "RTX A4000",
            ScreenWidth = 3840, ScreenHeight = 2160, Dpr = 1.5, Weight = 1,
        },
        new DeviceTemplate {
            Id = "amd_gaming_7900xt_2k",
            CpuCores = 24, RamGb = 32, GpuModel = "Radeon RX 7900 XT",
            ScreenWidth = 2560, ScreenHeight = 1440, Weight = 2,
        },
        new DeviceTemplate {
            Id = "gaming_laptop_rtx4060_oled", IsLaptop = true,
            CpuCores = 16, RamGb = 32, GpuModel = "GeForce RTX 4060 Laptop GPU",
            ScreenWidth = 2880, ScreenHeight = 1800, Dpr = 1.5, Weight = 2,
        },
        new DeviceTemplate {
            Id = "ultrabook_4k_32gb", IsLaptop = true,
            CpuCores = 16, RamGb = 32, GpuModel = "Arc(TM) Graphics",
            ScreenWidth = 3840, ScreenHeight = 2400, Dpr = 2.0, Weight = 2,
        },
        new DeviceTemplate {
            Id = "macbook_pro_16_m3_max", HumanName = "MacBook Pro 16 (M3 Max)",
            IsLaptop = true,
            CpuCores = 16, RamGb = 64, GpuModel = "Apple M3 Max",
            ScreenWidth = 1728, ScreenHeight = 1117, Dpr = 2.0, Weight = 2,
        },
        new DeviceTemplate {
            Id = "mac_studio_m2_ultra", HumanName = "Mac Studio (M2 Ultra)",
            CpuCores = 24, RamGb = 64, GpuModel = "Apple M2 Ultra",
            ScreenWidth = 3008, ScreenHeight = 1692, Dpr = 2.0, Weight = 1,
        },

        // ─── Mobile ────────────────────────────────────────────
        new DeviceTemplate {
            Id = "iphone_15_pro", HumanName = "Apple iPhone 15 Pro",
            FormFactor = FormFactor.Mobile,
            CpuCores = 6, RamGb = 8, GpuModel = "Apple GPU",
            ScreenWidth = 393, ScreenHeight = 852, Dpr = 3.0, Weight = 4,
        },
        new DeviceTemplate {
            Id = "iphone_16_pro_max", HumanName = "Apple iPhone 16 Pro Max",
            FormFactor = FormFactor.Mobile,
            CpuCores = 6, RamGb = 8, GpuModel = "Apple GPU",
            ScreenWidth = 440, ScreenHeight = 956, Dpr = 3.0, Weight = 3,
        },
        new DeviceTemplate {
            Id = "iphone_14", HumanName = "Apple iPhone 14",
            FormFactor = FormFactor.Mobile,
            CpuCores = 6, RamGb = 6, GpuModel = "Apple GPU",
            ScreenWidth = 390, ScreenHeight = 844, Dpr = 3.0, Weight = 4,
        },
        new DeviceTemplate {
            Id = "pixel_8_pro", HumanName = "Google Pixel 8 Pro",
            FormFactor = FormFactor.Mobile,
            CpuCores = 9, RamGb = 12, GpuModel = "Mali-G715",
            ScreenWidth = 412, ScreenHeight = 892, Dpr = 3.0, Weight = 3,
        },
        new DeviceTemplate {
            Id = "pixel_9", HumanName = "Google Pixel 9",
            FormFactor = FormFactor.Mobile,
            CpuCores = 8, RamGb = 12, GpuModel = "Mali-G715",
            ScreenWidth = 412, ScreenHeight = 915, Dpr = 2.625, Weight = 3,
        },
        new DeviceTemplate {
            Id = "galaxy_s24_ultra", HumanName = "Samsung Galaxy S24 Ultra",
            FormFactor = FormFactor.Mobile,
            CpuCores = 8, RamGb = 12, GpuModel = "Adreno 750",
            ScreenWidth = 412, ScreenHeight = 915, Dpr = 3.5, Weight = 3,
        },
        new DeviceTemplate {
            Id = "galaxy_s23", HumanName = "Samsung Galaxy S23",
            FormFactor = FormFactor.Mobile,
            CpuCores = 8, RamGb = 8, GpuModel = "Adreno 740",
            ScreenWidth = 360, ScreenHeight = 780, Dpr = 3.0, Weight = 3,
        },
        new DeviceTemplate {
            Id = "galaxy_a54", HumanName = "Samsung Galaxy A54",
            FormFactor = FormFactor.Mobile,
            CpuCores = 8, RamGb = 6, GpuModel = "Mali-G68",
            ScreenWidth = 360, ScreenHeight = 780, Dpr = 3.0, Weight = 3,
        },
        new DeviceTemplate {
            Id = "xiaomi_14", HumanName = "Xiaomi 14",
            FormFactor = FormFactor.Mobile,
            CpuCores = 8, RamGb = 12, GpuModel = "Adreno 750",
            ScreenWidth = 393, ScreenHeight = 873, Dpr = 3.0, Weight = 2,
        },
        new DeviceTemplate {
            Id = "redmi_note_13", HumanName = "Xiaomi Redmi Note 13",
            FormFactor = FormFactor.Mobile,
            CpuCores = 8, RamGb = 8, GpuModel = "Mali-G57",
            ScreenWidth = 393, ScreenHeight = 873, Dpr = 2.75, Weight = 3,
        },

        // ─── Tablet ────────────────────────────────────────────
        new DeviceTemplate {
            Id = "ipad_air_m2", HumanName = "Apple iPad Air (M2)",
            FormFactor = FormFactor.Tablet,
            CpuCores = 8, RamGb = 8, GpuModel = "Apple GPU",
            ScreenWidth = 820, ScreenHeight = 1180, Dpr = 2.0, Weight = 2,
        },
    };

    /// <summary>Lookup by <see cref="DeviceTemplate.Id"/>; null when unknown.</summary>
    public static DeviceTemplate? Find(string? id) =>
        string.IsNullOrEmpty(id)
            ? null
            : All.FirstOrDefault(t =>
                  string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
}

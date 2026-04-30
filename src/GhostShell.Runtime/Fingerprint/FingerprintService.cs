// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Fingerprint;

/// <summary>
/// Concrete <see cref="IFingerprintService"/>. Orchestrates payload
/// generation + scoring + salt persistence; SQL lives behind
/// <see cref="IFingerprintAuditService"/>.
/// </summary>
public sealed class FingerprintService : IFingerprintService
{
    private readonly IProfileService _profiles;
    private readonly IFingerprintAuditService _audits;
    private readonly ILogger<FingerprintService> _log;

    public FingerprintService(
        IProfileService profiles,
        IFingerprintAuditService audits,
        ILogger<FingerprintService> log)
    {
        _profiles = profiles;
        _audits   = audits;
        _log      = log;
    }

    public async Task<FingerprintScore> GetScoreAsync(string profileName, CancellationToken ct = default)
    {
        var profile = await _profiles.GetAsync(profileName, ct)
            ?? throw new InvalidOperationException($"Profile '{profileName}' not found");
        var template = ResolveTemplate(profile.TemplateId);
        var builder = new DeviceTemplateBuilder(
            profileName: profile.Name,
            template:    template,
            language:    profile.Language,
            timezoneId:  null,
            chromeMin:   null,
            chromeMax:   null,
            regenSalt:   profile.FpRegenSalt,
            noiseSalt:   profile.FpNoiseSalt);
        return CoherenceValidator.Validate(builder);
    }

    public async Task<FingerprintScore> RegenerateAsync(string profileName, CancellationToken ct = default)
    {
        var newSalt = Guid.NewGuid().ToString("N");
        await _audits.SetRegenSaltAsync(profileName, newSalt, ct);
        _log.LogInformation("Fingerprint regenerated for '{Profile}' (salt={Salt})",
            profileName, newSalt[..8]);
        var score = await GetScoreAsync(profileName, ct);
        await LogAuditAsync(profileName, score.Overall,
            (await _profiles.GetAsync(profileName, ct))?.TemplateId ?? "auto", ct);
        return score;
    }

    public async Task<FingerprintScore> ReshuffleAsync(string profileName, CancellationToken ct = default)
    {
        var newNoise = Guid.NewGuid().ToString("N");
        await _audits.SetNoiseSaltAsync(profileName, newNoise, ct);
        _log.LogInformation("Fingerprint reshuffled for '{Profile}' (noise={Salt})",
            profileName, newNoise[..8]);
        var score = await GetScoreAsync(profileName, ct);
        await LogAuditAsync(profileName, score.Overall,
            (await _profiles.GetAsync(profileName, ct))?.TemplateId ?? "auto", ct);
        return score;
    }

    public Task LogAuditAsync(string profileName, int score, string templateId, CancellationToken ct = default)
        => _audits.LogAsync(profileName, score, templateId, null, ct);

    public Task<IReadOnlyList<FingerprintAuditEntry>> ListAuditsAsync(
        string profileName, int limit = 50, CancellationToken ct = default)
        => _audits.ListAsync(profileName, limit, ct);

    private static DeviceTemplate ResolveTemplate(string? templateId)
    {
        var found = DeviceTemplateCatalog.All
            .FirstOrDefault(t => string.Equals(t.Id, templateId, StringComparison.OrdinalIgnoreCase));
        return found ?? new DeviceTemplate
        {
            Id          = "office_laptop_intel",
            HumanName   = "Office Laptop (Intel)",
            FormFactor  = FormFactor.Desktop,
            IsLaptop    = true,
            CpuCores    = 8,
            RamGb       = 16,
            GpuModel    = "Intel Iris Xe",
            ScreenWidth = 1920,
            ScreenHeight= 1080,
            Dpr         = 1.0,
            Weight      = 18,
        };
    }
}

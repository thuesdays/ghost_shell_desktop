// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net.Http;
using System.Text.Json;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Diagnostics;

/// <summary>
/// Default external reputation provider: a no-op. The product works fully
/// without any third-party fraud-score API — the heuristic scorer
/// (<see cref="ProxyReputationService"/>) carries the load. Registered
/// whenever no API key is configured.
/// </summary>
public sealed class NullIpReputationProvider : IIpReputationProvider
{
    public bool IsEnabled => false;
    public Task<ExternalReputation?> LookupAsync(string ip, CancellationToken ct = default)
        => Task.FromResult<ExternalReputation?>(null);
}

/// <summary>
/// IPQualityScore-backed reputation provider. Enabled only when an API key
/// is supplied (env <c>GHOSTSHELL_IPQS_KEY</c> or Settings). One free key
/// gives a few thousand lookups/month — plenty for pre-launch checks.
///
/// Endpoint: <c>https://ipqualityscore.com/api/json/ip/{KEY}/{IP}</c>
/// → JSON with <c>fraud_score</c> (0-100), <c>proxy</c>, <c>vpn</c>, <c>tor</c>.
/// </summary>
public sealed class IpQualityScoreProvider : IIpReputationProvider, IDisposable
{
    private readonly string? _apiKey;
    private readonly HttpClient _http;
    private readonly ILogger<IpQualityScoreProvider> _log;
    private readonly bool _ownsHttp;

    public IpQualityScoreProvider(
        string? apiKey,
        ILogger<IpQualityScoreProvider> log,
        HttpClient? http = null)
    {
        _apiKey   = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _log      = log;
        _ownsHttp = http is null;
        _http     = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    public bool IsEnabled => _apiKey is not null;

    public async Task<ExternalReputation?> LookupAsync(string ip, CancellationToken ct = default)
    {
        if (_apiKey is null || string.IsNullOrWhiteSpace(ip)) return null;

        var url = $"https://ipqualityscore.com/api/json/ip/{Uri.EscapeDataString(_apiKey)}/{Uri.EscapeDataString(ip)}?strictness=1&fast=1";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("IPQS HTTP {Code} for {Ip}", (int)resp.StatusCode, ip);
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("success", out var ok) &&
                ok.ValueKind == JsonValueKind.False)
            {
                _log.LogDebug("IPQS success=false for {Ip}", ip);
                return null;
            }
            int fraud = root.TryGetProperty("fraud_score", out var fs) && fs.TryGetInt32(out var f) ? f : 0;
            bool proxy = root.TryGetProperty("proxy", out var p) && p.ValueKind == JsonValueKind.True;
            bool vpn   = root.TryGetProperty("vpn",   out var v) && v.ValueKind == JsonValueKind.True;
            return new ExternalReputation(Math.Clamp(fraud, 0, 100), proxy, vpn, "IPQS");
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "IPQS lookup failed for {Ip}", ip);
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}

// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Scripts;

/// <summary>API-key config for <see cref="CapSolverSolver"/> (env
/// <c>GHOSTSHELL_CAPSOLVER_KEY</c> or Settings). Empty = disabled.</summary>
public sealed class CapSolverConfig
{
    public string ApiKey { get; init; } = "";
}

/// <summary>
/// CapSolver (capsolver.com) integration — a modern solver that, unlike the
/// 2captcha v1 here, covers Cloudflare Turnstile and reCAPTCHA v3 in addition
/// to reCAPTCHA v2 / hCaptcha. Wire format: POST /createTask then poll
/// /getTaskResult until <c>status:"ready"</c>, then inject the token via the
/// shared <see cref="CaptchaDetect.InjectTokenAsync"/>.
///
/// We use the *ProxyLess task variants: CapSolver solves from its own egress,
/// then we inject the token into our proxied browser. (Routing the solve
/// through the profile's proxy is possible by adding proxy fields to the task
/// but is rarely needed and costs more.)
/// </summary>
public sealed class CapSolverSolver : ICaptchaSolver
{
    private const string CreateUrl = "https://api.capsolver.com/createTask";
    private const string ResultUrl = "https://api.capsolver.com/getTaskResult";

    private readonly HttpClient _http;
    private readonly CapSolverConfig _cfg;
    private readonly ILogger<CapSolverSolver> _log;

    public CapSolverSolver(HttpClient http, CapSolverConfig cfg, ILogger<CapSolverSolver> log)
    {
        _http = http;
        _cfg  = cfg;
        _log  = log;
    }

    public string ProviderName => "capsolver";

    public bool IsAutomated => !string.IsNullOrWhiteSpace(_cfg.ApiKey);

    public bool CanHandle(string kind) =>
        kind is "recaptcha" or "recaptcha_v3" or "hcaptcha" or "turnstile";

    public Task<string?> DetectAsync(IBrowserSession session, CancellationToken ct = default)
        => CaptchaDetect.DetectAsync(session, ct);

    public async Task<bool> SolveAsync(
        IBrowserSession session, string kind, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_cfg.ApiKey))
        {
            _log.LogWarning("CapSolver key empty — solve aborted");
            return false;
        }
        if (!CanHandle(kind))
        {
            _log.LogInformation("CapSolver doesn't handle kind '{Kind}'", kind);
            return false;
        }

        var info = await CaptchaDetect.ExtractSiteKeyAsync(session, kind, ct);
        if (info is null)
        {
            _log.LogWarning("CapSolver: no sitekey on page (kind={Kind})", kind);
            return false;
        }
        if (kind == "recaptcha_v3" && string.IsNullOrEmpty(info.Value.Action))
            _log.LogDebug(
                "CapSolver: reCAPTCHA v3 with no detectable page action — solving with fallback 'verify'; " +
                "the token may score low if the site expects a specific action");

        var task = BuildTask(kind, info.Value);
        string taskId;
        try
        {
            taskId = await CreateTaskAsync(task, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "CapSolver createTask failed");
            return false;
        }
        _log.LogInformation("CapSolver task created: {Id} (kind={Kind})", taskId, kind);

        var deadline = DateTime.UtcNow + timeout;
        string? token = null;
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { return false; }
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                token = await PollAsync(taskId, ct);
                if (token is not null) break;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "CapSolver poll threw — retrying");
            }
            await Task.Delay(Random.Shared.Next(2500, 4000), ct);
        }
        if (token is null)
        {
            _log.LogWarning("CapSolver solve timed out (task {Id})", taskId);
            return false;
        }

        var filled = await CaptchaDetect.InjectTokenAsync(session, token, ct);
        if (filled <= 0)
        {
            _log.LogWarning("CapSolver token not injected — no response field on page (kind={Kind})", kind);
            return false;
        }
        _log.LogInformation("CapSolver token injected into {Count} field(s) (kind={Kind})", filled, kind);
        return true;
    }

    private Dictionary<string, object> BuildTask(string kind, CaptchaDetect.SiteKeyInfo info)
    {
        var (type, extra) = kind switch
        {
            "turnstile"    => ("AntiTurnstileTaskProxyLess", (Action<Dictionary<string, object>>)(_ => { })),
            "hcaptcha"     => ("HCaptchaTaskProxyLess",      _ => { }),
            "recaptcha_v3" => ("ReCaptchaV3TaskProxyLess",
                                d => { d["pageAction"] = info.Action ?? "verify"; }),
            _              => ("ReCaptchaV2TaskProxyLess",   _ => { }),
        };
        var task = new Dictionary<string, object>
        {
            ["type"]       = type,
            ["websiteURL"] = info.PageUrl,
            ["websiteKey"] = info.Sitekey,
        };
        extra(task);
        return task;
    }

    private async Task<string> CreateTaskAsync(Dictionary<string, object> task, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["clientKey"] = _cfg.ApiKey, ["task"] = task };
        using var resp = await _http.PostAsJsonAsync(CreateUrl, payload, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CapResponse>(cancellationToken: ct);
        if (body is null || body.ErrorId != 0 || string.IsNullOrEmpty(body.TaskId))
            throw new InvalidOperationException(
                $"CapSolver createTask error: {body?.ErrorCode} {body?.ErrorDescription}");
        return body.TaskId!;
    }

    private async Task<string?> PollAsync(string taskId, CancellationToken ct)
    {
        var payload = new Dictionary<string, object> { ["clientKey"] = _cfg.ApiKey, ["taskId"] = taskId };
        using var resp = await _http.PostAsJsonAsync(ResultUrl, payload, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<CapResponse>(cancellationToken: ct);
        if (body is null) return null;
        if (body.ErrorId != 0)
            throw new InvalidOperationException($"CapSolver error: {body.ErrorCode} {body.ErrorDescription}");
        if (!string.Equals(body.Status, "ready", StringComparison.OrdinalIgnoreCase))
            return null; // "processing"
        // Token lives under solution.gRecaptchaResponse (recaptcha/hcaptcha)
        // or solution.token (turnstile).
        if (body.Solution is null) return null;
        if (body.Solution.Value.TryGetProperty("gRecaptchaResponse", out var g) && g.ValueKind == JsonValueKind.String)
            return g.GetString();
        if (body.Solution.Value.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
            return t.GetString();
        return null;
    }

    private sealed class CapResponse
    {
        [JsonPropertyName("errorId")]          public int ErrorId { get; init; }
        [JsonPropertyName("errorCode")]        public string? ErrorCode { get; init; }
        [JsonPropertyName("errorDescription")] public string? ErrorDescription { get; init; }
        [JsonPropertyName("taskId")]           public string? TaskId { get; init; }
        [JsonPropertyName("status")]           public string? Status { get; init; }
        [JsonPropertyName("solution")]         public JsonElement? Solution { get; init; }
    }
}

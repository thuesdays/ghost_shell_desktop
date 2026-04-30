// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// Default <see cref="ICaptchaSolver"/>: pauses the script and polls
/// the DOM until the captcha disappears (the user solves it manually
/// in the live browser window). Most stable / least error-prone for
/// v1 — paid providers (2captcha, anticaptcha) come in Phase 14.
///
/// Detection is heuristic: looks for the iframe / <c>div.g-recaptcha</c>
/// / Cloudflare challenge markers. Same shape as the
/// captcha_visible condition kind so the two paths stay consistent.
/// </summary>
public sealed class ManualCaptchaSolver : ICaptchaSolver
{
    private readonly ILogger<ManualCaptchaSolver> _log;

    public ManualCaptchaSolver(ILogger<ManualCaptchaSolver> log) { _log = log; }

    public string ProviderName => "manual";

    public async Task<string?> DetectAsync(IBrowserSession session, CancellationToken ct = default)
    {
        const string Js = """
            return (function() {
              if (document.querySelector('iframe[src*="recaptcha"]')) return 'recaptcha';
              if (document.querySelector('iframe[src*="hcaptcha"]'))  return 'hcaptcha';
              if (document.querySelector('div.g-recaptcha'))           return 'recaptcha';
              if (document.querySelector('div.h-captcha'))             return 'hcaptcha';
              if (document.querySelector('#cf-challenge-running'))     return 'cloudflare';
              if (/verifying you are human|are you a robot/i.test(
                    document.body && document.body.innerText || ''))   return 'unknown';
              return null;
            })();
        """;
        try
        {
            return await session.ExecuteScriptAsync(Js, null, ct) as string;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Captcha detect threw");
            return null;
        }
    }

    public async Task<bool> SolveAsync(
        IBrowserSession session, string kind, TimeSpan timeout,
        CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        _log.LogInformation(
            "Captcha '{Kind}' detected — waiting up to {Sec}s for manual solve",
            kind, timeout.TotalSeconds);

        // Poll every 1.5-2.5s with per-call jitter. Without jitter,
        // 100 simultaneous warmups + script runs all hit the
        // detect-call at exactly the same wall-clock instant, causing
        // CDP-bridge contention and an SQLite-write storm if any
        // probe persists state. Random spread distributes the load.
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var jittered = Random.Shared.Next(1500, 2501);
            await Task.Delay(jittered, ct);
            var stillThere = await DetectAsync(session, ct);
            if (stillThere is null)
            {
                _log.LogInformation("Captcha cleared (kind={Kind})", kind);
                return true;
            }
        }
        _log.LogWarning("Captcha solve timed out (kind={Kind}, waited {Sec}s)",
            kind, timeout.TotalSeconds);
        return false;
    }
}

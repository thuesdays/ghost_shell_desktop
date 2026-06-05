// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Collections.Concurrent;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Scripts;

/// <summary>
/// The <see cref="ICaptchaSolver"/> the runner actually talks to. Detects the
/// captcha kind once, then routes to the best provider:
///   1. automated solvers that are configured (have a key) AND handle the kind,
///      in preference order (CapSolver → 2captcha — CapSolver covers more
///      types incl. Turnstile / reCAPTCHA v3);
///   2. the manual solver as a last resort (waits for the user to solve it).
///
/// Special case: Google's "sorry" / network-block interstitial is NOT a
/// solvable widget — solving rarely sticks and just burns credits. The router
/// refuses it and returns false so the caller rotates the IP (the real fix —
/// see Feature #1).
///
/// Tracks per-provider success/failure counts so the user can see which solver
/// is actually earning its keep.
/// </summary>
public sealed class CaptchaSolverRouter : ICaptchaSolver
{
    private readonly ICaptchaSolver _manual;
    private readonly IReadOnlyList<ICaptchaSolver> _automated;
    private readonly ILogger<CaptchaSolverRouter> _log;
    private readonly ConcurrentDictionary<string, int[]> _metrics = new(); // [ok, fail]

    /// <summary>
    /// Single constructor (kept single on purpose — a second ctor taking an
    /// <see cref="ICaptchaSolver"/> made MS-DI pick it and resolve a circular
    /// ICaptchaSolver→router→ICaptchaSolver chain). DI builds
    /// <paramref name="automated"/> explicitly from the concrete provider types
    /// via a factory; tests pass fakes the same way.
    /// </summary>
    public CaptchaSolverRouter(
        ICaptchaSolver manual,
        IReadOnlyList<ICaptchaSolver> automated,
        ILogger<CaptchaSolverRouter> log)
    {
        _manual    = manual;
        _automated = automated;
        _log       = log;
    }

    public string ProviderName => "router";

    public Task<string?> DetectAsync(IBrowserSession session, CancellationToken ct = default)
        => CaptchaDetect.DetectAsync(session, ct);

    public async Task<bool> SolveAsync(
        IBrowserSession session, string kind, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(kind)) return false;

        // Network-block interstitials aren't solvable — bail so the caller
        // rotates the proxy instead of wasting solver credits.
        if (kind is "sorry" or "cloudflare")
        {
            _log.LogWarning(
                "Captcha router: '{Kind}' is a network-block interstitial, not a solvable widget — " +
                "rotate the proxy (see IP-reputation gate). Not attempting a solve.", kind);
            return false;
        }

        var tried = false;
        foreach (var solver in _automated)
        {
            if (!solver.IsAutomated || !solver.CanHandle(kind)) continue;
            tried = true;
            _log.LogInformation("Captcha router → {Provider} for kind '{Kind}'", solver.ProviderName, kind);
            bool ok;
            try { ok = await solver.SolveAsync(session, kind, timeout, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Captcha router: {Provider} threw", solver.ProviderName);
                ok = false;
            }
            Record(solver.ProviderName, ok);
            if (ok) return true;
        }

        // Fall back to the manual/interactive solver.
        if (!tried)
            _log.LogInformation(
                "Captcha router: no automated solver configured for '{Kind}' — falling back to manual " +
                "(set GHOSTSHELL_CAPSOLVER_KEY or GHOSTSHELL_2CAPTCHA_KEY for unattended solving)", kind);
        else
            _log.LogInformation("Captcha router: automated solvers failed for '{Kind}' — falling back to manual", kind);

        bool manualOk;
        try { manualOk = await _manual.SolveAsync(session, kind, timeout, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, "Captcha router: manual solver threw"); manualOk = false; }
        Record(_manual.ProviderName, manualOk);
        return manualOk;
    }

    private void Record(string provider, bool ok)
    {
        var slot = _metrics.GetOrAdd(provider, _ => new int[2]);
        Interlocked.Increment(ref slot[ok ? 0 : 1]);
    }

    /// <summary>Per-provider (successes, failures) snapshot for diagnostics.</summary>
    public IReadOnlyDictionary<string, (int Ok, int Fail)> Metrics =>
        _metrics.ToDictionary(kv => kv.Key, kv => (kv.Value[0], kv.Value[1]));
}

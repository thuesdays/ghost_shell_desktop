// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using System.Net.Http;
using GhostShell.Core.Models;
using GhostShell.Core.Services;
using Microsoft.Extensions.Logging;

namespace GhostShell.Runtime.Recovery;

/// <summary>
/// Phase 71ii — concrete recovery brain. See
/// <see cref="ICaptchaRecoveryService"/> for the contract.
///
/// <para>The decision tree, summarised:</para>
/// <code>
///   captcha hit
///     │
///     ├── this profile hit 5+ captchas/hour?  (CAPTCHA-02: profile-scoped,
///     │    NOT a proxy-wide count — siblings on a shared rotating proxy
///     │    must not pause a healthy profile)
///     │    YES ──▶ L5 Exhausted: pause profile + critical notification
///     │
///     ├── proxy has rotation API?
///     │    YES ──▶ count captchas this proxy/last hour
///     │              0       → L1 Light:        rotate IP
///     │              1–2     → L2 Moderate:     rotate IP + skip restore
///     │              3+      → L3 Aggressive:   rotate IP + skip restore + FP regen
///     │
///     │    NO  ──▶ L4 NoProxyFallback: skip restore + FP regen + longer cooldown
///     │            (this profile at 5+/hour still escalates to L5)
/// </code>
///
/// <para>Every action is best-effort and individually safe to fail —
/// the service downgrades gracefully and reports what actually ran
/// in <see cref="CaptchaRecoveryResult"/>. The proxy-rotation HTTP
/// call has a 15s timeout, mirrors the launcher's auto-rotate path.</para>
/// </summary>
public sealed class CaptchaRecoveryService : ICaptchaRecoveryService
{
    // ─── Dependency note ──────────────────────────────────────────
    // Notably absent: IProfileRunner. The recovery service WAS taking
    // IProfileRunner so it could call MarkSkipRestoreOnce(...) inline,
    // but that creates a DI cycle —
    //   IProfileRunner → IScriptRunner → ICaptchaRecoveryService → IProfileRunner.
    // Solution: skip-restore is the ONE recovery action that doesn't
    // need any side-effects beyond a single flag set, and the catch
    // block in RealProfileRunner that consumes our result already has
    // an IProfileRunner instance (itself). So we just plan the action
    // — set Plan.SkipRestoreOnNextLaunch=true — and let RealProfileRunner
    // execute it from its catch handler. Mirror semantics for the
    // SkipRestoreFlagSet result field: it reflects the plan, not a
    // call we made (RealProfileRunner sets it on the runner side).
    private readonly IProfileService      _profiles;
    private readonly IProxyService?       _proxies;
    private readonly IProxyHealthService? _proxyHealth;
    private readonly IFingerprintService? _fingerprint;
    private readonly INotificationService? _notifications;
    private readonly ILogger<CaptchaRecoveryService> _log;

    /// <summary>5+ captchas in 60 minutes for the same profile is the
    /// "exhausted, please intervene" threshold — we've already tried
    /// rotate + skip-restore + FP regen and they keep getting walled.</summary>
    private const int ExhaustionThresholdPerHour = 5;

    public CaptchaRecoveryService(
        IProfileService profiles,
        ILogger<CaptchaRecoveryService> log,
        IProxyService? proxies = null,
        IProxyHealthService? proxyHealth = null,
        IFingerprintService? fingerprint = null,
        INotificationService? notifications = null)
    {
        _profiles     = profiles;
        _proxies      = proxies;
        _proxyHealth  = proxyHealth;
        _fingerprint  = fingerprint;
        _notifications = notifications;
        _log          = log;
    }

    // ──────────────────────────────────────────────────────────────
    // Probe
    // ──────────────────────────────────────────────────────────────

    public async Task<bool> IsCaptchaPageAsync(IBrowserSession session, CancellationToken ct = default)
    {
        if (session is null) return false;
        try
        {
            // One short JS probe — fastest way to fingerprint the page
            // without round-tripping multiple ExecuteScript calls. Each
            // signature is independently sufficient; ANY hit is a wall.
            //
            // Signatures, in order of strongest-to-weakest:
            //   1. URL contains "/sorry/index" — Google's "this looks
            //      like automated traffic" wall, redirects all SERP
            //      requests until you click a captcha checkbox.
            //   2. recaptcha iframe — script.google.com/recaptcha
            //      injects an iframe with src starting with that URL.
            //   3. captcha-form id — older Google captcha pages still
            //      use this id on the form element.
            //   4. "unusual traffic" / "Our systems have detected" body
            //      copy — Google's English captcha page text. Falls
            //      apart on localised Russian/Ukrainian variants but
            //      catches the vast majority of cases.
            // audit CAPTCHA-01: ExecuteScript wraps this source in a
            // function body and only marshals a value back to C# when the
            // body uses a top-level `return`. The IIFE expression alone
            // discards its result (returns undefined → null), so every
            // captcha page was reported as "not a captcha" and recovery
            // never fired. Prefix with `return`, matching the pattern in
            // ManualCaptchaSolver/TwoCaptchaSolver.DetectAsync.
            const string js = @"
                return (function() {
                    try {
                        var u = location.href || '';
                        if (u.indexOf('/sorry/index') >= 0) return 'sorry_url';
                        if (u.indexOf('/sorry/') >= 0)      return 'sorry_path';
                        if (document.querySelector('iframe[src*=""recaptcha""]')) return 'recaptcha_iframe';
                        if (document.getElementById('captcha-form')) return 'captcha_form_id';
                        var t = (document.body && document.body.innerText) || '';
                        if (t.indexOf('unusual traffic') >= 0) return 'body_unusual_traffic';
                        if (t.indexOf('Our systems have detected') >= 0) return 'body_systems_detected';
                        if (t.indexOf('Подозрительный трафик') >= 0) return 'body_ru';
                        if (t.indexOf('незвичний трафік') >= 0) return 'body_uk';
                        return null;
                    } catch (e) { return null; }
                })();
            ";

            var hit = await session.ExecuteScriptAsync(js, null, ct);
            if (hit is string s && !string.IsNullOrEmpty(s))
            {
                _log.LogInformation("CaptchaRecovery: detection signature '{Sig}' tripped", s);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Probe should never tank the run on its own. A swallowed
            // exception means "couldn't determine" → treat as not-a-
            // captcha and let downstream retry logic handle it.
            _log.LogDebug(ex, "CaptchaRecovery: probe threw, treating as not-captcha");
            return false;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Plan + execute
    // ──────────────────────────────────────────────────────────────

    public async Task<CaptchaRecoveryResult> HandleAsync(
        CaptchaIncident incident, CancellationToken ct = default)
    {
        if (incident is null) throw new ArgumentNullException(nameof(incident));

        // 1) Look up profile + proxy. Both may be null/missing — the
        //    plan-builder downgrades gracefully.
        Profile? profile = null;
        Proxy?   proxy   = null;
        try { profile = await _profiles.GetAsync(incident.ProfileName, ct); }
        catch (Exception ex) { _log.LogDebug(ex, "CaptchaRecovery: profile lookup failed"); }

        if (profile is { ProxySlug: { Length: > 0 } slug } && _proxies is not null)
        {
            try { proxy = await _proxies.GetAsync(slug, ct); }
            catch (Exception ex) { _log.LogDebug(ex, "CaptchaRecovery: proxy lookup failed"); }
        }

        // 2) Count recent captchas — proxy-scoped (drives the L1–L3
        //    rotation ladder) and profile-scoped (drives the L5
        //    exhaustion/pause decision). audit CAPTCHA-02: the two are
        //    now genuinely distinct — the profile count filters events
        //    by the profile stamp instead of mirroring the proxy count.
        var (proxyCaptchasLastHour, profileCaptchasLastHour) =
            await CountRecentCaptchasAsync(profile, proxy, ct);

        // 3) Build the plan based on inputs.
        var plan = BuildPlan(profile, proxy, proxyCaptchasLastHour, profileCaptchasLastHour);
        _log.LogInformation(
            "CaptchaRecovery: profile='{P}' severity={Sev} reason='{R}' " +
            "(captchas this profile/hr={Pf}, this proxy/hr={Px})",
            incident.ProfileName, plan.Severity, plan.Reason,
            profileCaptchasLastHour, proxyCaptchasLastHour);

        // 4) Execute the plan. Each action is independently safe to
        //    fail; the result records what actually ran.
        return await ExecutePlanAsync(plan, incident, profile, proxy, ct);
    }

    // ──────────────────────────────────────────────────────────────
    // Plan builder
    // ──────────────────────────────────────────────────────────────

    private static CaptchaRecoveryPlan BuildPlan(
        Profile? profile, Proxy? proxy,
        int proxyCaptchasLastHour, int profileCaptchasLastHour)
    {
        // Exhaustion check first — beats any proxy-availability path.
        // 5+ captchas in 60 minutes means the moves we'd normally take
        // (rotate, skip restore, regen FP) already failed at least
        // 4 times. Asking the user is the right move.
        //
        // audit CAPTCHA-02: exhaustion is PROFILE-scoped, not a Max over
        // proxy+profile. The threshold means "THIS profile keeps getting
        // walled" (see the ExhaustionThresholdPerHour doc-comment). Using
        // Max(proxy, profile) let captchas from OTHER profiles sharing a
        // rotating residential proxy force a healthy profile straight to
        // L5 (pause + critical alert). Now we gate on this profile's own
        // count; high proxy-wide pressure is handled by the proxy-scoped
        // L3 ladder below (full reset), which is the right response to a
        // noisy exit IP without pausing a profile that's behaving.
        if (profileCaptchasLastHour >= ExhaustionThresholdPerHour)
        {
            return new CaptchaRecoveryPlan
            {
                Severity                = RecoverySeverity.Exhausted,
                SkipRestoreOnNextLaunch = true,    // give the next run a clean slate at least
                RotateProxy             = HasUsableRotation(proxy),
                RegenerateFingerprint   = true,
                AutoRelaunch            = false,   // let the user take over
                Cooldown                = TimeSpan.FromMinutes(5),
                Reason                  = $"{profileCaptchasLastHour} captchas for this profile in last hour — pausing auto-retry, manual attention recommended",
                CaptchasLastHour        = profileCaptchasLastHour,
                ProxyCaptchasLastHour   = proxyCaptchasLastHour,
            };
        }

        // No usable rotation → L4 fallback. Do everything else we can
        // and back off longer.
        if (!HasUsableRotation(proxy))
        {
            return new CaptchaRecoveryPlan
            {
                Severity                = RecoverySeverity.NoProxyFallback,
                SkipRestoreOnNextLaunch = true,
                RotateProxy             = false,   // can't
                RegenerateFingerprint   = true,
                AutoRelaunch            = true,
                Cooldown                = TimeSpan.FromSeconds(45),
                Reason                  = proxy is null
                    ? "no proxy bound — skipping restore + regenerating fingerprint, relaunch in 45s"
                    : "proxy has no rotation API — skipping restore + regenerating fingerprint, relaunch in 45s",
                CaptchasLastHour        = profileCaptchasLastHour,
                ProxyCaptchasLastHour   = proxyCaptchasLastHour,
            };
        }

        // Normal escalation ladder.
        if (proxyCaptchasLastHour >= 3)
        {
            // L3 Aggressive — same proxy hitting captchas repeatedly.
            // Cookies probably stale, FP probably weak; reset both.
            return new CaptchaRecoveryPlan
            {
                Severity                = RecoverySeverity.Aggressive,
                SkipRestoreOnNextLaunch = true,
                RotateProxy             = true,
                RegenerateFingerprint   = true,
                AutoRelaunch            = true,
                Cooldown                = TimeSpan.FromSeconds(30),
                Reason                  = $"{proxyCaptchasLastHour} captchas via this proxy/hr — full reset (rotate + clean cookies + new FP)",
                CaptchasLastHour        = profileCaptchasLastHour,
                ProxyCaptchasLastHour   = proxyCaptchasLastHour,
            };
        }
        if (proxyCaptchasLastHour >= 1)
        {
            // L2 Moderate — second/third hit, probably poisoned cookies
            // riding a freshly-rotated IP. Drop them, rotate again.
            return new CaptchaRecoveryPlan
            {
                Severity                = RecoverySeverity.Moderate,
                SkipRestoreOnNextLaunch = true,
                RotateProxy             = true,
                RegenerateFingerprint   = false,
                AutoRelaunch            = true,
                Cooldown                = TimeSpan.FromSeconds(20),
                Reason                  = $"{proxyCaptchasLastHour + 1} captchas this hr — rotating IP and skipping snapshot restore",
                CaptchasLastHour        = profileCaptchasLastHour,
                ProxyCaptchasLastHour   = proxyCaptchasLastHour,
            };
        }

        // L1 Light — first hit on a fresh proxy. Most often this just
        // means the rotated-into IP was rate-limited; the cheapest fix
        // is another rotation.
        return new CaptchaRecoveryPlan
        {
            Severity                = RecoverySeverity.Light,
            SkipRestoreOnNextLaunch = false,
            RotateProxy             = true,
            RegenerateFingerprint   = false,
            AutoRelaunch            = true,
            Cooldown                = TimeSpan.FromSeconds(10),
            Reason                  = "first captcha of the hour — rotating IP and retrying with same identity",
            CaptchasLastHour        = profileCaptchasLastHour,
            ProxyCaptchasLastHour   = proxyCaptchasLastHour,
        };
    }

    private static bool HasUsableRotation(Proxy? proxy) =>
        proxy is { IsRotating: true } &&
        !string.IsNullOrWhiteSpace(proxy.RotationApiUrl);

    // ──────────────────────────────────────────────────────────────
    // Plan executor
    // ──────────────────────────────────────────────────────────────

    private async Task<CaptchaRecoveryResult> ExecutePlanAsync(
        CaptchaRecoveryPlan plan, CaptchaIncident incident,
        Profile? profile, Proxy? proxy, CancellationToken ct)
    {
        bool rotationAttempted = false;
        bool rotationSucceeded = false;
        // ── 1. Skip-restore on next launch — DEFERRED ──────────────
        // The plan declares the intent; the actual MarkSkipRestoreOnce
        // call happens in RealProfileRunner's catch handler (it has
        // its own IProfileRunner reference — namely itself — so we
        // dodge the DI cycle that came from injecting IProfileRunner
        // into the recovery service). See the dependency note at the
        // top of the class. From here forward we just propagate the
        // intent through the result so the caller knows what to do.
        bool skipRestoreSet    = plan.SkipRestoreOnNextLaunch;
        bool fpRegenerated     = false;
        bool notificationPosted = false;

        // ── 2. Proxy rotation ──────────────────────────────────────
        if (plan.RotateProxy && HasUsableRotation(proxy))
        {
            rotationAttempted = true;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var resp = await http.GetAsync(proxy!.RotationApiUrl, ct);
                resp.EnsureSuccessStatusCode();
                rotationSucceeded = true;
                _log.LogInformation(
                    "CaptchaRecovery: rotated proxy '{Slug}' (HTTP {Code})",
                    proxy.Slug, (int)resp.StatusCode);

                if (_proxyHealth is not null)
                {
                    await _proxyHealth.RecordAsync(new ProxyHealthEvent
                    {
                        ProxySlug = proxy.Slug,
                        Kind      = ProxyHealthEventKind.Rotation,
                        At        = DateTime.UtcNow,
                        Detail    = $"auto-rotated after captcha (severity={plan.Severity})",
                    }, ct);
                }

                // Give the upstream rotation a beat to settle.
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "CaptchaRecovery: rotation HTTP call failed for proxy '{Slug}'",
                    proxy?.Slug ?? "?");

                // Rotation failed → record a Burn event (the IP
                // refused to rotate) so the timeline shows the
                // problem and the ProxyService health badges flip
                // appropriately.
                if (_proxyHealth is not null && proxy is not null)
                {
                    try
                    {
                        await _proxyHealth.RecordAsync(new ProxyHealthEvent
                        {
                            ProxySlug = proxy.Slug,
                            Kind      = ProxyHealthEventKind.Burn,
                            At        = DateTime.UtcNow,
                            Detail    = $"rotation failed: {ex.GetType().Name}: {Truncate(ex.Message, 200)}",
                        }, ct);
                    }
                    catch { /* non-critical; swallow */ }
                }

                // Don't crash recovery; fall through to FP regen.
            }
        }
        else if (plan.RotateProxy)
        {
            // Plan asked us to rotate but the proxy doesn't support
            // it — log loudly. This shouldn't happen because the
            // plan-builder gates rotation on HasUsableRotation, but
            // the explicit branch keeps the contract clear.
            _log.LogWarning(
                "CaptchaRecovery: plan requested rotation but proxy '{Slug}' lacks RotationApiUrl",
                proxy?.Slug ?? "(none)");
        }

        // ── 3. Fingerprint regen ───────────────────────────────────
        if (plan.RegenerateFingerprint && _fingerprint is not null)
        {
            try
            {
                var score = await _fingerprint.RegenerateAsync(incident.ProfileName, ct);
                fpRegenerated = true;
                _log.LogInformation(
                    "CaptchaRecovery: regenerated fingerprint for '{P}' (new score {S})",
                    incident.ProfileName, score.Overall);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "CaptchaRecovery: fingerprint regen threw for '{P}'",
                    incident.ProfileName);
            }
        }

        // ── 4. Captcha event on the proxy timeline ─────────────────
        // This always fires — even on the rotation-unavailable path —
        // because the user wants to see the captcha happened.
        if (_proxyHealth is not null && proxy is not null)
        {
            try
            {
                // audit CAPTCHA-02: stamp the profile name into the event
                // detail so CountRecentCaptchasAsync can derive a TRUE
                // per-profile count instead of copying the proxy count.
                // Several profiles routinely share one rotating residential
                // proxy, so the profile dimension must be filtered, not
                // assumed equal to the proxy dimension. The token is
                // machine-readable ("profile='<name>'") yet still reads
                // fine in the timeline tooltip.
                var detail = $"profile='{Truncate(incident.ProfileName, 80)}'; severity={plan.Severity}";
                if (!string.IsNullOrEmpty(incident.Query))
                    detail += $"; query='{Truncate(incident.Query, 60)}'";
                if (!string.IsNullOrEmpty(incident.ExitIp))
                    detail += $"; ip={incident.ExitIp}";
                await _proxyHealth.RecordAsync(new ProxyHealthEvent
                {
                    ProxySlug = proxy.Slug,
                    Kind      = ProxyHealthEventKind.Captcha,
                    At        = DateTime.UtcNow,
                    Detail    = detail,
                }, ct);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "CaptchaRecovery: proxy-health captcha event failed");
            }
        }

        // ── 5. User-visible notification ───────────────────────────
        notificationPosted = await PostNotificationAsync(plan, incident, proxy, ct);

        // ── 6. Decide auto-relaunch ────────────────────────────────
        // The plan asks for it AND we managed at least one meaningful
        // action (otherwise relaunching would just hit the same wall).
        bool didSomething = skipRestoreSet || rotationSucceeded || fpRegenerated;
        bool autoRelaunch = plan.AutoRelaunch && didSomething;

        var summary =
            $"severity={plan.Severity}; " +
            $"skipRestore={(skipRestoreSet ? "set" : "no")}; " +
            $"rotate={(rotationAttempted ? (rotationSucceeded ? "ok" : "failed") : "no")}; " +
            $"fpRegen={(fpRegenerated ? "ok" : "no")}; " +
            $"relaunch={(autoRelaunch ? plan.Cooldown.TotalSeconds.ToString("0") + "s" : "off")}";

        return new CaptchaRecoveryResult
        {
            Plan                    = plan,
            ProxyRotationAttempted  = rotationAttempted,
            ProxyRotationSucceeded  = rotationSucceeded,
            SkipRestoreFlagSet      = skipRestoreSet,
            FingerprintRegenerated  = fpRegenerated,
            NotificationPosted      = notificationPosted,
            AutoRelaunchRequested   = autoRelaunch,
            Summary                 = summary,
        };
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private async Task<(int proxyHr, int profileHr)> CountRecentCaptchasAsync(
        Profile? profile, Proxy? proxy, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddHours(-1);
        int proxyHr = 0;
        int profileHr = 0;

        if (_proxyHealth is null || proxy is null) return (0, 0);

        // audit CAPTCHA-02: derive a TRUE per-profile count by matching
        // the "profile='<name>'" token we now stamp into captcha event
        // details (see ExecutePlanAsync step 4). Previously profileHr was
        // just a copy of proxyHr, so siblings sharing a rotating proxy
        // could force a healthy profile straight to L5 Exhausted. We
        // build the match token once and look for it as a substring so
        // the count is independent of the rest of the detail format.
        string? profileToken = profile is { Name: { Length: > 0 } pname }
            ? $"profile='{Truncate(pname, 80)}'"
            : null;

        try
        {
            var events = await _proxyHealth.ListForProxyAsync(proxy.Slug, since, ct);
            foreach (var ev in events)
            {
                if (ev.Kind != ProxyHealthEventKind.Captcha) continue;
                proxyHr++;
                // Count toward THIS profile only when the event carries
                // our profile stamp. Legacy events recorded before this
                // fix have no token and so contribute to proxy pressure
                // only — the conservative, correct default.
                if (profileToken is not null &&
                    ev.Detail is { Length: > 0 } d &&
                    d.Contains(profileToken, StringComparison.Ordinal))
                {
                    profileHr++;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "CaptchaRecovery: ListForProxyAsync threw");
        }

        return (proxyHr, profileHr);
    }

    private async Task<bool> PostNotificationAsync(
        CaptchaRecoveryPlan plan, CaptchaIncident incident, Proxy? proxy, CancellationToken ct)
    {
        if (_notifications is null) return false;
        try
        {
            var severity = plan.Severity switch
            {
                RecoverySeverity.Exhausted     => "critical",
                RecoverySeverity.Aggressive    => "warning",
                RecoverySeverity.NoProxyFallback => "warning",
                _ => "info",
            };
            var title = plan.Severity == RecoverySeverity.Exhausted
                ? $"Profile '{incident.ProfileName}' is hitting captchas repeatedly"
                : $"Captcha auto-recovery on '{incident.ProfileName}'";
            var body  = plan.Reason;
            if (!string.IsNullOrEmpty(incident.Query))
                body += $" (query: {Truncate(incident.Query, 60)})";
            if (proxy is { Slug: { Length: > 0 } slug })
                body += $" • proxy: {slug}";

            await _notifications.AddAsync(
                severity, title, body,
                action: "open_profile", actionArg: incident.ProfileName,
                source: "captcha_recovery", ct: ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "CaptchaRecovery: notification post threw");
            return false;
        }
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" :
        (s.Length <= max ? s : s.Substring(0, max - 1) + "…");
}

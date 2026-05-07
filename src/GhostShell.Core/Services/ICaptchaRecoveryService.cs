// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Core.Services;

/// <summary>
/// Phase 71ii — smart, multi-level captcha recovery.
///
/// <para>The pre-71ii flow was strictly linear: detect captcha →
/// blindly skip-restore on next launch → hope. That left a lot of
/// recoverable cases on the table — sometimes the cookies are fine
/// and only the IP burned, sometimes the IP is fine but the FP
/// salted into the run looks weak, sometimes the proxy isn't even
/// rotatable so "rotate IP" isn't an option at all.</para>
///
/// <para>The recovery service replaces that linear flow with a
/// decision tree. Inputs:
/// <list type="bullet">
///   <item>Number of captchas seen for this profile/proxy in the
///         last hour and last 24h.</item>
///   <item>Profile fingerprint score (low → FP rotation candidate).</item>
///   <item>Whether the bound proxy declares a rotation API.</item>
///   <item>Whether the proxy is reachable / was just rotated.</item>
///   <item>How many launches in a row hit captchas (exhaustion).</item>
/// </list>
/// Output: a <see cref="CaptchaRecoveryPlan"/> indicating the
/// minimum-viable recovery action. Smaller actions first (just rotate
/// IP); escalate to skip-restore + FP regen as the same proxy keeps
/// catching captchas; finally pause the profile for manual attention
/// if the system has run out of moves.</para>
///
/// <para>The service also <em>executes</em> the plan — drives the
/// proxy rotation HTTP call, flags skip-restore-once on the runner,
/// bumps the FP regen salt, records the proxy-health Captcha event,
/// and posts a notification with severity matched to the plan.</para>
/// </summary>
public interface ICaptchaRecoveryService
{
    /// <summary>
    /// Probe the live page for known Google bot-wall signatures —
    /// /sorry/index URL, recaptcha iframe, captcha-form id, "unusual
    /// traffic" body text. Best-effort; returns <c>false</c> on probe
    /// failure (don't trigger recovery on a transient JS hiccup).
    /// </summary>
    Task<bool> IsCaptchaPageAsync(IBrowserSession session, CancellationToken ct = default);

    /// <summary>
    /// Build + execute a recovery plan for a profile that just hit a
    /// captcha. Idempotent in the sense that calling it twice for the
    /// same incident produces the same plan (the plan is recomputed
    /// from current state, not cached). Logs at info level for plan
    /// summary, warning for skipped actions due to misconfiguration.
    /// </summary>
    Task<CaptchaRecoveryResult> HandleAsync(
        CaptchaIncident incident, CancellationToken ct = default);
}

/// <summary>
/// Inputs describing one captcha hit. Created by
/// <see cref="ScriptRunner"/> at the moment of detection.
/// </summary>
public sealed record CaptchaIncident
{
    public required string ProfileName { get; init; }

    /// <summary>The query / URL / hint that hit the captcha. Goes
    /// straight into the proxy-health event detail field for
    /// diagnostic value.</summary>
    public string? Query { get; init; }

    /// <summary>Optional run id — stamped onto the proxy-health event
    /// so the timeline links back to the specific run.</summary>
    public long? RunId { get; init; }

    /// <summary>Best-effort exit IP at the moment of capture, for
    /// timeline detail. Pass null if unknown.</summary>
    public string? ExitIp { get; init; }
}

/// <summary>
/// Strategy ladder. Smaller numbers = lighter touch; larger numbers
/// = more invasive. The service starts at the minimum needed to
/// resolve the symptom and escalates only when prior attempts at the
/// same proxy/profile already failed.
/// </summary>
public enum RecoverySeverity
{
    /// <summary>L1 — single captcha, fresh proxy. Just rotate IP.</summary>
    Light,

    /// <summary>L2 — captcha pattern emerging. Rotate IP + drop the
    /// possibly-poisoned cookies for the next launch.</summary>
    Moderate,

    /// <summary>L3 — multiple captchas in succession on the same
    /// proxy. Rotate IP + skip restore + bump FP regen salt for a
    /// fresh-but-deterministic identity.</summary>
    Aggressive,

    /// <summary>L4 — proxy has no rotation API or rotation just
    /// failed. Apply everything we still can (skip restore + FP
    /// regen) and back off longer before retry.</summary>
    NoProxyFallback,

    /// <summary>L5 — out of moves. Captcha rate so high (5+/hour) the
    /// system pauses retries and surfaces a critical notification so
    /// the user can intervene (swap proxy, give the profile a rest).</summary>
    Exhausted,
}

/// <summary>
/// What the service decided to do. Each flag corresponds to one
/// recovery action; the executor walks them in dependency order and
/// flips fields on the result describing what actually ran.
/// </summary>
public sealed record CaptchaRecoveryPlan
{
    public required RecoverySeverity Severity { get; init; }

    /// <summary>True → flag the runner to drop snapshot restore on
    /// the next launch of this profile.</summary>
    public bool SkipRestoreOnNextLaunch { get; init; }

    /// <summary>True → fire the proxy's RotationApiUrl HTTP GET
    /// before the next launch.</summary>
    public bool RotateProxy { get; init; }

    /// <summary>True → bump <c>profiles.fp_regen_salt</c>.</summary>
    public bool RegenerateFingerprint { get; init; }

    /// <summary>True → kick off a re-launch automatically once the
    /// current run finalises. Otherwise the user (or the schedule)
    /// drives the next launch.</summary>
    public bool AutoRelaunch { get; init; }

    /// <summary>How long the runner should wait before auto-relaunch
    /// to let proxy rotation settle / avoid dog-piling the target.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Plain-English reason summarising the decision. Goes
    /// into the log line and the notification body.</summary>
    public string Reason { get; init; } = "";

    /// <summary>How many captchas this profile has racked up in the
    /// last hour. Stored on the plan for telemetry.</summary>
    public int CaptchasLastHour { get; init; }

    /// <summary>How many on the bound proxy in the last hour. Same.</summary>
    public int ProxyCaptchasLastHour { get; init; }
}

/// <summary>
/// What the executor actually managed to do. Each flag mirrors a
/// plan field but reflects success — proxy rotation might be
/// requested by the plan but fail at HTTP time, in which case the
/// service downgrades to a fallback severity and re-runs.
/// </summary>
public sealed record CaptchaRecoveryResult
{
    public required CaptchaRecoveryPlan Plan { get; init; }

    public bool ProxyRotationAttempted  { get; init; }
    public bool ProxyRotationSucceeded  { get; init; }

    /// <summary>True when the plan calls for skip-restore on the next
    /// launch. The actual flag-setting happens in
    /// <c>RealProfileRunner</c>'s recovery-abort catch handler — we
    /// can't call <c>MarkSkipRestoreOnce</c> from this service without
    /// taking IProfileRunner, which would close a DI cycle through
    /// IScriptRunner. So we declare intent here and let the runner
    /// (which IS the IProfileRunner) execute it.</summary>
    public bool SkipRestoreFlagSet      { get; init; }

    public bool FingerprintRegenerated  { get; init; }
    public bool NotificationPosted      { get; init; }

    /// <summary>True → caller (RealProfileRunner) should kick the
    /// profile again after <see cref="CaptchaRecoveryPlan.Cooldown"/>.</summary>
    public bool AutoRelaunchRequested   { get; init; }

    public string Summary { get; init; } = "";
}

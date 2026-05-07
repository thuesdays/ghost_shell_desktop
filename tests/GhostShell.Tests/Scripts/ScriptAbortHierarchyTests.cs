// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using GhostShell.Runtime.Scripts;
using Xunit;

namespace GhostShell.Tests.Scripts;

/// <summary>
/// Phase 71ii regression — pin down the script-abort exception
/// hierarchy that the captcha-recovery flow depends on. The big risks:
///
///   1. CaptchaRecoveryAbortException MUST derive from
///      ScriptAbortException — otherwise the existing
///      <c>catch (ScriptAbortException)</c> in RealProfileRunner won't
///      pick up captcha aborts and the run row won't finalise.
///
///   2. ScriptAbortException MUST NOT be sealed — for the same reason.
///      A previous version of the class was `public sealed class` and
///      the build broke when CaptchaRecoveryAbortException tried to
///      derive from it. The tests pin "non-sealed" so a future tidy-up
///      can't regress that.
///
///   3. The wrapped result is non-null and surfaces via the public
///      Result property so the catch handler in the launcher can
///      apply the recovery plan without round-tripping through
///      Exception.Data dictionaries.
/// </summary>
public class ScriptAbortHierarchyTests
{
    [Fact]
    public void ScriptAbortException_IsNotSealed()
    {
        // sealed = no inheritors. A future "let's seal everything" PR
        // would silently break the captcha-recovery catch path.
        Assert.False(typeof(ScriptRunner.ScriptAbortException).IsSealed);
    }

    [Fact]
    public void CaptchaRecoveryAbortException_DerivesFromScriptAbortException()
    {
        // The launcher's existing catch (ScriptAbortException) needs
        // to pick up captcha-recovery aborts the same way it picks up
        // step-level aborts. Inheritance is the contract.
        Assert.True(typeof(ScriptRunner.ScriptAbortException)
            .IsAssignableFrom(typeof(ScriptRunner.CaptchaRecoveryAbortException)));
    }

    [Fact]
    public void CaptchaRecoveryAbortException_CarriesResultIntact()
    {
        var plan = new CaptchaRecoveryPlan
        {
            Severity                = RecoverySeverity.Aggressive,
            SkipRestoreOnNextLaunch = true,
            RotateProxy             = true,
            RegenerateFingerprint   = true,
            AutoRelaunch            = true,
            Cooldown                = TimeSpan.FromSeconds(30),
            Reason                  = "test",
        };
        var result = new CaptchaRecoveryResult
        {
            Plan                   = plan,
            ProxyRotationAttempted = true,
            ProxyRotationSucceeded = true,
            SkipRestoreFlagSet     = true,
            FingerprintRegenerated = true,
            NotificationPosted     = true,
            AutoRelaunchRequested  = true,
            Summary                = "ok",
        };

        var ex = new ScriptRunner.CaptchaRecoveryAbortException(result);

        // Result must be the SAME instance — the catch handler reads
        // it directly. A defensive-copy here would silently break
        // mutation-based tests (we don't actually mutate, but the
        // contract is "you get back what you passed in").
        Assert.Same(result, ex.Result);
        Assert.Equal(RecoverySeverity.Aggressive, ex.Result.Plan.Severity);
        Assert.True(ex.Result.AutoRelaunchRequested);
    }

    [Fact]
    public void CaptchaRecoveryAbortException_MessageContainsSummary()
    {
        // The exception's Message should include the recovery summary
        // for log-line readability — the existing fallback catch in
        // RealProfileRunner only logs Message + StackTrace, not Result.
        var result = new CaptchaRecoveryResult
        {
            Plan = new CaptchaRecoveryPlan
            {
                Severity = RecoverySeverity.Light,
                Reason   = "first hit",
            },
            Summary = "severity=Light; rotate=ok",
        };
        var ex = new ScriptRunner.CaptchaRecoveryAbortException(result);
        Assert.Contains("severity=Light", ex.Message);
        Assert.Contains("rotate=ok",      ex.Message);
    }

    [Fact]
    public void CaughtAsScriptAbortException_ExposesRecoveryResult_ViaTypeCheck()
    {
        // Mirrors the actual launcher code path — handler catches
        // ScriptAbortException then pattern-matches for the recovery
        // subtype. The pattern match must succeed.
        var plan = new CaptchaRecoveryPlan { Severity = RecoverySeverity.Moderate };
        var result = new CaptchaRecoveryResult { Plan = plan };
        ScriptRunner.ScriptAbortException ex
            = new ScriptRunner.CaptchaRecoveryAbortException(result);

        bool matched = false;
        if (ex is ScriptRunner.CaptchaRecoveryAbortException recovery)
        {
            matched = true;
            Assert.Equal(RecoverySeverity.Moderate, recovery.Result.Plan.Severity);
        }
        Assert.True(matched, "pattern match must succeed");
    }
}

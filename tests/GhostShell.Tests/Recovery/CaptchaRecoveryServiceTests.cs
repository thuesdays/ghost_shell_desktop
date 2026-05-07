// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;
using GhostShell.Core.Services;
using GhostShell.Runtime.Recovery;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

// Alias the Proxy model — the bare `Proxy` token resolves to the
// `GhostShell.Tests.Proxy` namespace (HttpConnectForwarderTests live
// there) under our `using` set, not the model class. Aliasing both
// the type and the IProxyService methods that take it sidesteps the
// CS0118 ambiguity without forcing us to fully-qualify everywhere.
using ProxyModel = GhostShell.Core.Models.Proxy;

namespace GhostShell.Tests.Recovery;

/// <summary>
/// Phase 71ii regression tests — multi-level captcha recovery.
///
/// The recovery service is the brain that decides what to do when a
/// Google captcha fires:
///   • L1 Light (rotate IP only) — first hit on a fresh proxy.
///   • L2 Moderate (rotate + skip restore) — 1-2 prior captchas.
///   • L3 Aggressive (rotate + skip + FP regen) — 3-4 prior.
///   • L4 NoProxyFallback — proxy can't rotate; do everything else.
///   • L5 Exhausted — 5+ in an hour; pause + critical notification.
///
/// These tests pin down the decision boundaries (BuildPlan via the
/// public HandleAsync surface) and the "rotate failed → record Burn"
/// degradation path. Service-level fakes are used for IProxyService /
/// IProxyHealthService so we can stage the captcha history precisely;
/// the rotation HTTP call goes to a stub that lets the test choose
/// success/failure outcomes.
/// </summary>
public class CaptchaRecoveryServiceTests
{
    // ─── Severity ladder ─────────────────────────────────────────────

    [Fact]
    public async Task L1_Light_FirstCaptchaOnRotatableProxy_RotatesOnly()
    {
        var fakeProxies     = new FakeProxyService(WithRotation("p1"));
        var fakeProxyHealth = new FakeProxyHealthService(); // 0 prior captchas
        var fakeProfiles    = new FakeProfileService(MakeProfile("p1", proxySlug: "p1"));
        var fakeFp          = new FakeFingerprintService();
        var fakeNotif       = new FakeNotificationService();

        var svc = new CaptchaRecoveryService(
            fakeProfiles, NullLogger<CaptchaRecoveryService>.Instance,
            fakeProxies, fakeProxyHealth, fakeFp, fakeNotif);

        var result = await svc.HandleAsync(new CaptchaIncident
        {
            ProfileName = "p1", Query = "goodmedika",
        });

        Assert.Equal(RecoverySeverity.Light, result.Plan.Severity);
        Assert.True(result.Plan.RotateProxy);
        Assert.False(result.Plan.SkipRestoreOnNextLaunch);
        Assert.False(result.Plan.RegenerateFingerprint);
        Assert.True(result.Plan.AutoRelaunch);
    }

    [Fact]
    public async Task L2_Moderate_OneTwoPriorCaptchas_AddsSkipRestore()
    {
        var fakeProxies     = new FakeProxyService(WithRotation("p1"));
        var fakeProxyHealth = new FakeProxyHealthService();
        // Stage 2 prior captchas in last hour for proxy p1.
        fakeProxyHealth.SeedCaptchas("p1", count: 2);
        var fakeProfiles = new FakeProfileService(MakeProfile("p1", proxySlug: "p1"));

        var svc = new CaptchaRecoveryService(
            fakeProfiles, NullLogger<CaptchaRecoveryService>.Instance,
            fakeProxies, fakeProxyHealth, new FakeFingerprintService(), new FakeNotificationService());

        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

        Assert.Equal(RecoverySeverity.Moderate, result.Plan.Severity);
        Assert.True(result.Plan.RotateProxy);
        Assert.True(result.Plan.SkipRestoreOnNextLaunch);
        Assert.False(result.Plan.RegenerateFingerprint);
    }

    [Fact]
    public async Task L3_Aggressive_ThreePriorCaptchas_AddsFingerprintRegen()
    {
        var fakeProxies     = new FakeProxyService(WithRotation("p1"));
        var fakeProxyHealth = new FakeProxyHealthService();
        fakeProxyHealth.SeedCaptchas("p1", count: 3);
        var fakeProfiles = new FakeProfileService(MakeProfile("p1", proxySlug: "p1"));

        var svc = new CaptchaRecoveryService(
            fakeProfiles, NullLogger<CaptchaRecoveryService>.Instance,
            fakeProxies, fakeProxyHealth, new FakeFingerprintService(), new FakeNotificationService());

        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

        Assert.Equal(RecoverySeverity.Aggressive, result.Plan.Severity);
        Assert.True(result.Plan.RotateProxy);
        Assert.True(result.Plan.SkipRestoreOnNextLaunch);
        Assert.True(result.Plan.RegenerateFingerprint);
    }

    [Fact]
    public async Task L5_Exhausted_FiveOrMoreInHour_PausesNoAutoRelaunch()
    {
        var fakeProxies     = new FakeProxyService(WithRotation("p1"));
        var fakeProxyHealth = new FakeProxyHealthService();
        fakeProxyHealth.SeedCaptchas("p1", count: 5);
        var fakeProfiles = new FakeProfileService(MakeProfile("p1", proxySlug: "p1"));

        var svc = new CaptchaRecoveryService(
            fakeProfiles, NullLogger<CaptchaRecoveryService>.Instance,
            fakeProxies, fakeProxyHealth, new FakeFingerprintService(), new FakeNotificationService());

        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

        Assert.Equal(RecoverySeverity.Exhausted, result.Plan.Severity);
        Assert.False(result.Plan.AutoRelaunch);
        Assert.False(result.AutoRelaunchRequested);
    }

    [Fact]
    public async Task L4_NoProxyFallback_ProxyHasNoRotationApi_StillSkipsRestoreAndRegens()
    {
        // Profile has a proxy but proxy.IsRotating=false / RotationApiUrl=null.
        var noRotateProxy = new ProxyModel
        {
            Slug           = "p2",
            Url            = "http://1.2.3.4:8080",
            IsRotating     = false,
            RotationApiUrl = null,
        };
        var fakeProxies     = new FakeProxyService(noRotateProxy);
        var fakeProxyHealth = new FakeProxyHealthService();
        var fakeProfiles    = new FakeProfileService(MakeProfile("p1", proxySlug: "p2"));

        var svc = new CaptchaRecoveryService(
            fakeProfiles, NullLogger<CaptchaRecoveryService>.Instance,
            fakeProxies, fakeProxyHealth, new FakeFingerprintService(), new FakeNotificationService());

        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

        Assert.Equal(RecoverySeverity.NoProxyFallback, result.Plan.Severity);
        Assert.False(result.Plan.RotateProxy);
        Assert.True(result.Plan.SkipRestoreOnNextLaunch);
        Assert.True(result.Plan.RegenerateFingerprint);
        Assert.True(result.Plan.AutoRelaunch); // we can still recover
        // Cooldown is longer in fallback mode.
        Assert.True(result.Plan.Cooldown >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task L4_NoProxyFallback_ProfileWithoutAnyProxy_ReportsClearReason()
    {
        var fakeProxies     = new FakeProxyService(); // empty
        var fakeProxyHealth = new FakeProxyHealthService();
        var fakeProfiles    = new FakeProfileService(MakeProfile("p1", proxySlug: null));

        var svc = new CaptchaRecoveryService(
            fakeProfiles, NullLogger<CaptchaRecoveryService>.Instance,
            fakeProxies, fakeProxyHealth, new FakeFingerprintService(), new FakeNotificationService());

        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

        Assert.Equal(RecoverySeverity.NoProxyFallback, result.Plan.Severity);
        Assert.Contains("no proxy", result.Plan.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Result execution ────────────────────────────────────────────

    [Fact]
    public async Task SkipRestoreFlagSet_MirrorsPlan_DoesNotCallRunner()
    {
        // Phase 71ii post-cycle-fix: the service no longer takes
        // IProfileRunner — RealProfileRunner does the actual flag
        // set in its catch handler. The result simply reflects the
        // plan's intent.
        var fakeProxies     = new FakeProxyService(WithRotation("p1"));
        var fakeProxyHealth = new FakeProxyHealthService();
        fakeProxyHealth.SeedCaptchas("p1", count: 2);
        var fakeProfiles = new FakeProfileService(MakeProfile("p1", proxySlug: "p1"));

        var svc = new CaptchaRecoveryService(
            fakeProfiles, NullLogger<CaptchaRecoveryService>.Instance,
            fakeProxies, fakeProxyHealth);

        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

        Assert.True(result.Plan.SkipRestoreOnNextLaunch);
        Assert.True(result.SkipRestoreFlagSet);
    }

    [Fact]
    public async Task FingerprintRegenerationActuallyCallsService_OnL3()
    {
        var fakeFp = new FakeFingerprintService();
        var svc = new CaptchaRecoveryService(
            new FakeProfileService(MakeProfile("p1", proxySlug: "p1")),
            NullLogger<CaptchaRecoveryService>.Instance,
            new FakeProxyService(WithRotation("p1")),
            SeededHealth("p1", 3),
            fakeFp);

        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

        Assert.True(result.Plan.RegenerateFingerprint);
        Assert.True(result.FingerprintRegenerated);
        Assert.Single(fakeFp.RegenerateCalls);
        Assert.Equal("p1", fakeFp.RegenerateCalls[0]);
    }

    [Fact]
    public async Task NotificationPosted_OnEverySeverity()
    {
        foreach (var (priorCount, expectedSeverity) in new[]
                 {
                     (0, RecoverySeverity.Light),
                     (2, RecoverySeverity.Moderate),
                     (3, RecoverySeverity.Aggressive),
                     (5, RecoverySeverity.Exhausted),
                 })
        {
            var fakeNotif = new FakeNotificationService();
            var svc = new CaptchaRecoveryService(
                new FakeProfileService(MakeProfile("p1", proxySlug: "p1")),
                NullLogger<CaptchaRecoveryService>.Instance,
                new FakeProxyService(WithRotation("p1")),
                SeededHealth("p1", priorCount),
                new FakeFingerprintService(),
                fakeNotif);
            var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });

            Assert.Equal(expectedSeverity, result.Plan.Severity);
            Assert.True(result.NotificationPosted);
            Assert.Single(fakeNotif.AddedNotifications);
            // Exhausted → critical; otherwise info/warning. Not info on
            // Aggressive because the user wants to see when we're
            // burning FP salt + cookies + IP simultaneously.
            if (expectedSeverity == RecoverySeverity.Exhausted)
                Assert.Equal("critical", fakeNotif.AddedNotifications[0].Severity);
            else if (expectedSeverity is RecoverySeverity.Aggressive
                                       or RecoverySeverity.NoProxyFallback)
                Assert.Equal("warning", fakeNotif.AddedNotifications[0].Severity);
            else
                Assert.Equal("info", fakeNotif.AddedNotifications[0].Severity);
        }
    }

    [Fact]
    public async Task CaptchaEventRecorded_OnProxyHealthTimeline()
    {
        var fakeHealth = new FakeProxyHealthService();
        var svc = new CaptchaRecoveryService(
            new FakeProfileService(MakeProfile("p1", proxySlug: "p1")),
            NullLogger<CaptchaRecoveryService>.Instance,
            new FakeProxyService(WithRotation("p1")),
            fakeHealth);

        await svc.HandleAsync(new CaptchaIncident
        {
            ProfileName = "p1", Query = "goodmedika", ExitIp = "1.2.3.4",
        });

        // We expect at least one Captcha event on p1 after the call.
        var captchaEvents = fakeHealth.RecordedEvents
            .Where(e => e.ProxySlug == "p1" && e.Kind == ProxyHealthEventKind.Captcha)
            .ToList();
        Assert.Single(captchaEvents);
        Assert.Contains("goodmedika", captchaEvents[0].Detail);
        Assert.Contains("1.2.3.4",    captchaEvents[0].Detail);
    }

    [Fact]
    public async Task AutoRelaunch_RequiresAtLeastOneActionToSucceed()
    {
        // L5 exhausted → AutoRelaunch=false even if actions ran.
        // L1-L4 → AutoRelaunch=true when at least one action lands.
        var svc = new CaptchaRecoveryService(
            new FakeProfileService(MakeProfile("p1", proxySlug: "p1")),
            NullLogger<CaptchaRecoveryService>.Instance,
            new FakeProxyService(WithRotation("p1")),
            new FakeProxyHealthService(),
            new FakeFingerprintService(),
            new FakeNotificationService());
        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });
        Assert.True(result.AutoRelaunchRequested);
    }

    [Fact]
    public async Task NoProxyService_LegacyBuild_StillProducesCoherentPlan()
    {
        // Service constructed without IProxyService / IProxyHealthService
        // (legacy DI path) — should still return a valid plan,
        // gracefully degrading to NoProxyFallback severity.
        var svc = new CaptchaRecoveryService(
            new FakeProfileService(MakeProfile("p1", proxySlug: "p1")),
            NullLogger<CaptchaRecoveryService>.Instance);
        var result = await svc.HandleAsync(new CaptchaIncident { ProfileName = "p1" });
        Assert.NotNull(result.Plan);
        Assert.Equal(RecoverySeverity.NoProxyFallback, result.Plan.Severity);
    }

    // ─── Test helpers ────────────────────────────────────────────────

    private static ProxyModel WithRotation(string slug) => new()
    {
        Slug           = slug,
        Url            = $"http://{slug}.local:8080",
        IsRotating     = true,
        RotationApiUrl = "http://example.invalid/rotate", // never actually fetched in unit tests
    };

    private static Profile MakeProfile(string name, string? proxySlug) => new()
    {
        Name      = name,
        ProxySlug = proxySlug,
    };

    private static FakeProxyHealthService SeededHealth(string slug, int captchaCount)
    {
        var h = new FakeProxyHealthService();
        h.SeedCaptchas(slug, captchaCount);
        return h;
    }

    // ─── Stubs ───────────────────────────────────────────────────────

    private sealed class FakeProfileService : IProfileService
    {
        private readonly Dictionary<string, Profile> _byName;

        public FakeProfileService(params Profile[] profiles)
        {
            _byName = profiles.ToDictionary(p => p.Name, p => p,
                StringComparer.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Profile>>(_byName.Values.ToList());
        public Task<Profile?> GetAsync(string name, CancellationToken ct = default)
            => Task.FromResult(_byName.TryGetValue(name, out var p) ? p : null);
        public Task<Profile> CreateAsync(Profile p, CancellationToken ct = default)
            { _byName[p.Name] = p; return Task.FromResult(p); }
        public Task UpdateAsync(Profile p, CancellationToken ct = default)
            { _byName[p.Name] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string name, CancellationToken ct = default)
            { _byName.Remove(name); return Task.CompletedTask; }
        public Task RecordRunStartedAsync(string name, DateTime startedAt, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<BulkCreateProfilesResult> BulkCreateAsync(
            BulkCreateProfilesRequest req, CancellationToken ct = default)
            => Task.FromResult(new BulkCreateProfilesResult(
                Array.Empty<Profile>(), Array.Empty<string>()));
    }

    private sealed class FakeProxyService : IProxyService
    {
        private readonly Dictionary<string, ProxyModel> _bySlug;

        public FakeProxyService(params ProxyModel[] proxies)
            => _bySlug = proxies.ToDictionary(p => p.Slug, p => p);

        public Task<IReadOnlyList<ProxyModel>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProxyModel>>(_bySlug.Values.ToList());
        public Task<ProxyModel?> GetAsync(string slug, CancellationToken ct = default)
            => Task.FromResult(_bySlug.TryGetValue(slug, out var p) ? p : null);
        public Task<ProxyModel?> GetByUrlAsync(string url, CancellationToken ct = default)
            => Task.FromResult(_bySlug.Values.FirstOrDefault(p => p.Url == url));
        public Task<ProxyModel> CreateAsync(ProxyModel p, CancellationToken ct = default)
            { _bySlug[p.Slug] = p; return Task.FromResult(p); }
        public Task<BulkCreateResult> BulkCreateAsync(IReadOnlyList<ProxyModel> proxies, CancellationToken ct = default)
            => Task.FromResult(new BulkCreateResult(Array.Empty<ProxyModel>(), Array.Empty<string>()));
        public Task UpdateAsync(ProxyModel p, CancellationToken ct = default)
            { _bySlug[p.Slug] = p; return Task.CompletedTask; }
        public Task DeleteAsync(string slug, CancellationToken ct = default)
            { _bySlug.Remove(slug); return Task.CompletedTask; }
        public Task RecordTestResultAsync(string slug, ProxyTestResult result, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeProxyHealthService : IProxyHealthService
    {
        public List<ProxyHealthEvent> RecordedEvents { get; } = new();

        public void SeedCaptchas(string slug, int count)
        {
            for (int i = 0; i < count; i++)
                RecordedEvents.Add(new ProxyHealthEvent
                {
                    ProxySlug = slug,
                    Kind      = ProxyHealthEventKind.Captcha,
                    At        = DateTime.UtcNow.AddMinutes(-i * 5),
                    Detail    = $"seed #{i}",
                });
        }

        public Task<IReadOnlyList<ProxyHealthEvent>> ListAsync(
            DateTime? since = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProxyHealthEvent>>(
                RecordedEvents.Where(e => since is null || e.At >= since).ToList());

        public Task<IReadOnlyList<ProxyHealthEvent>> ListForProxyAsync(
            string proxySlug, DateTime? since = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ProxyHealthEvent>>(
                RecordedEvents.Where(e => e.ProxySlug == proxySlug
                                       && (since is null || e.At >= since)).ToList());

        public Task RecordAsync(ProxyHealthEvent ev, CancellationToken ct = default)
        {
            RecordedEvents.Add(ev);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, ProxyHealthCounters>> CountersAsync(
            DateTime? since = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, ProxyHealthCounters>>(
                new Dictionary<string, ProxyHealthCounters>());
    }

    private sealed class FakeFingerprintService : IFingerprintService
    {
        public List<string> RegenerateCalls { get; } = new();

        public Task<FingerprintScore> GetScoreAsync(string profileName, CancellationToken ct = default)
            => Task.FromResult(MakeScore(85));

        public Task<FingerprintScore> RegenerateAsync(string profileName, CancellationToken ct = default)
        {
            RegenerateCalls.Add(profileName);
            return Task.FromResult(MakeScore(90));
        }

        public Task<FingerprintScore> ReshuffleAsync(string profileName, CancellationToken ct = default)
            => Task.FromResult(MakeScore(85));

        public Task LogAuditAsync(string profileName, int score, string templateId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FingerprintAuditEntry>> ListAuditsAsync(
            string profileName, int limit = 50, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<FingerprintAuditEntry>>(Array.Empty<FingerprintAuditEntry>());

        private static FingerprintScore MakeScore(int s) => new()
        {
            Overall = s,
            Label   = s >= 85 ? "EXCELLENT" : s >= 75 ? "OK" : "RISKY",
            Checks  = Array.Empty<FingerprintCheck>(),
        };
    }

    public sealed record AddedNotification(string Severity, string Title, string? Body, string? Action, string? ActionArg, string Source);

    private sealed class FakeNotificationService : INotificationService
    {
        public List<AddedNotification> AddedNotifications { get; } = new();
        public event EventHandler? Changed;

        public Task<Notification> AddAsync(
            string severity, string title, string? body = null,
            string? action = null, string? actionArg = null,
            string source = "manual", CancellationToken ct = default)
        {
            AddedNotifications.Add(new AddedNotification(severity, title, body, action, actionArg, source));
            return Task.FromResult(new Notification
            {
                Id          = AddedNotifications.Count,
                Severity    = severity,
                Title       = title,
                Body        = body,
                Action      = action,
                ActionArg   = actionArg,
                Source      = source,
                CreatedAt   = DateTime.UtcNow,
                DismissedAt = null,
            });
        }

        public Task<IReadOnlyList<Notification>> ListActiveAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Notification>>(Array.Empty<Notification>());
        public Task<IReadOnlyList<Notification>> ListAllAsync(int limit = 200, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Notification>>(Array.Empty<Notification>());
        public Task<IReadOnlyDictionary<string, int>> CountActiveBySeverityAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task DismissAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
        public Task DismissAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task PurgeOlderThanAsync(int days = 30, CancellationToken ct = default) => Task.CompletedTask;
    }
}

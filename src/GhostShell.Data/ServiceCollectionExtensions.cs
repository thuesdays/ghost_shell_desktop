// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Services;
using GhostShell.Data.Database;
using GhostShell.Data.Services;
using Microsoft.Extensions.DependencyInjection;

namespace GhostShell.Data;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers persistence: connection (singleton, holds the SQLite
    /// handle), migration runner, and the three Phase-1 services.
    /// </summary>
    public static IServiceCollection AddGhostShellData(this IServiceCollection services)
    {
        services.AddSingleton<DatabaseConnection>();
        services.AddSingleton<MigrationRunner>();
        // Phase 31 hot-fix — one-shot migrator that re-derives every
        // extension's ext_id with the current SynthesizeExtId algorithm
        // (UTF-16 LE on Windows). Wired in App.xaml.cs right after the
        // SQL migration runner finishes.
        services.AddSingleton<ExtensionIdMigrator>();

        services.AddSingleton<IProfileService,     ProfileService>();
        services.AddSingleton<IRunService,         RunService>();
        services.AddSingleton<IProxyService,       ProxyService>();
        services.AddSingleton<IProxyHealthService, ProxyHealthService>();

        // Phase 4.2 — Sessions & Cookies feature port. Both services
        // share the same DatabaseConnection / QueueAsync gate as the
        // rest, so concurrent UI navigation between Sessions / Packs /
        // Profiles serialises through the same semaphore.
        services.AddSingleton<ISessionService,     SessionService>();
        services.AddSingleton<ICookiePackService,  CookiePackService>();

        // Phase 4.5 — Profile Groups (batch-launch).
        services.AddSingleton<IProfileGroupService, ProfileGroupService>();

        // Phase 5 — Scheduler. Schedule rows are persisted; the runner
        // host hosts the tick loop that fires due rows.
        services.AddSingleton<IScheduleService, ScheduleService>();

        // Phase 6 — Warmup robot. The history service owns warmup_runs
        // SQL; the orchestration class (WarmupService) lives in
        // GhostShell.Runtime and is registered in App startup because
        // it depends on IBrowserLauncher.
        services.AddSingleton<IWarmupHistoryService, WarmupHistoryService>();

        // Phase 9 — Fingerprint salts + audit log persistence. The
        // orchestration class (FingerprintService) lives in
        // GhostShell.Runtime so it can pull DeviceTemplateBuilder.
        services.AddSingleton<IFingerprintAuditService, FingerprintAuditService>();

        // Phase 11 — Self-check probe history (network-layer probes
        // run by the Runtime SelfCheckService). Same split pattern.
        services.AddSingleton<ISelfCheckHistoryService, SelfCheckHistoryService>();

        // Phase 12 — Scripts library + run history.
        services.AddSingleton<IScriptService, ScriptService>();

        // Phase 24 — Credential vault. Internal class registered as
        // singleton so the master-key + lock state persists across
        // VM activations.
        services.AddSingleton<IVaultService, VaultService>();

        // Phase 27 — Browser Extensions. Singleton so the extension
        // library is shared across pages; HttpClient is supplied by
        // Microsoft.Extensions.Http (registered in App.xaml.cs).
        services.AddSingleton<IExtensionService, ExtensionService>();

        // Phase 28 — Traffic accounting. Singleton so the dashboard
        // and the per-profile collector share the same write path.
        services.AddSingleton<ITrafficService, TrafficService>();

        // Phase 29 — Settings (key/value config) + Notifications.
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<INotificationService, NotificationService>();

        // Phase 31 — external fingerprint-tester probe persistence.
        services.AddSingleton<IExternalTesterResultService, ExternalTesterResultService>();

        // Phase 34 — Domain-list management (my / target / block).
        services.AddSingleton<IDomainListService, DomainListService>();

        // Phase 34 — competitor records + analytics.
        services.AddSingleton<ICompetitorService, CompetitorService>();

        // Phase 34 — Ad density + Overview widget layout.
        services.AddSingleton<IAdDensityService, AdDensityService>();
        services.AddSingleton<IOverviewLayoutService, OverviewLayoutService>();

        // Phase 35 — GitHub-based self-update. HttpClient is supplied by
        // Microsoft.Extensions.Http (registered in App.xaml.cs).
        services.AddSingleton<IUpdateService, GitHubUpdateService>();

        return services;
    }
}

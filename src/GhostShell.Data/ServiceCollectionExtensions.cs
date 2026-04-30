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

        return services;
    }
}

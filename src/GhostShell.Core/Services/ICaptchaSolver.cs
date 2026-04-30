// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Services;

/// <summary>
/// Interface for captcha-solving providers. v1 ships
/// <c>ManualCaptchaSolver</c> only — it pauses the script and polls
/// the DOM until the captcha disappears (the user solves it inside
/// the live browser window). 2captcha / anticaptcha implementations
/// are stubbed at <see cref="ProviderName"/> level so a future
/// settings page can swap providers without touching the runner.
///
/// Solver contract:
///   • <see cref="DetectAsync"/> returns a captcha kind ("recaptcha"
///     / "hcaptcha" / "cloudflare" / "unknown") OR null when no
///     captcha is present.
///   • <see cref="SolveAsync"/> blocks until the captcha clears or
///     times out. Returns true on solve, false on timeout/failure.
/// </summary>
public interface ICaptchaSolver
{
    string ProviderName { get; }

    Task<string?> DetectAsync(IBrowserSession session, CancellationToken ct = default);

    Task<bool> SolveAsync(
        IBrowserSession session, string kind,
        TimeSpan timeout, CancellationToken ct = default);
}

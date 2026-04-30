// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// One browser identity. Maps roughly to a row in the legacy
/// `profiles` table — fields are kept name-compatible so a future
/// import tool can copy rows verbatim.
/// </summary>
public sealed class Profile
{
    public required string Name { get; init; }

    /// <summary>Free-form group label (e.g. "research", "monitor-medika").</summary>
    public string? GroupName { get; init; }

    /// <summary>
    /// Device template id from <see cref="DeviceTemplateCatalog"/>.
    /// <c>null</c> = "auto" (random pick at run time).
    /// </summary>
    public string? TemplateId { get; init; }

    /// <summary>
    /// Preferred language tag (Accept-Language / navigator.language).
    /// e.g. "uk-UA", "en-US". <c>null</c> = system default.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>Optional proxy slug — null means "no proxy / direct".</summary>
    public string? ProxySlug { get; init; }

    /// <summary>True if the profile is generated and ready to launch.</summary>
    public bool IsReady { get; init; }

    /// <summary>
    /// Seed realistic browsing history on first run (top sites,
    /// bookmarks). Strongly recommended — without it the very first
    /// visit looks like a brand-new browser, which is a known
    /// fingerprint signal.
    /// </summary>
    public bool EnrichOnFirstRun { get; init; } = true;

    /// <summary>Timestamp of the profile's most recent run, if any.</summary>
    public DateTime? LastRunAt { get; init; }

    /// <summary>Total number of runs this profile has accumulated.</summary>
    public int RunCount { get; init; }

    /// <summary>Free-form note shown in the UI.</summary>
    public string? Note { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    // ─── Phase 9: Fingerprint salts ────────────────────────────────
    // The fingerprint payload is deterministic from (Name, FpRegenSalt,
    // FpNoiseSalt). Changing FpRegenSalt → full regeneration; changing
    // FpNoiseSalt → noise-only reshuffle.
    public string? FpRegenSalt { get; init; }
    public string? FpNoiseSalt { get; init; }

    // ─── Phase 13: Script binding ──────────────────────────────────
    // When set, RealProfileRunner kicks the assigned script against
    // the live session right after launch. Null = no script (warmup
    // only, or pure manual control).
    public long? AssignedScriptId { get; init; }

    // ─── Phase 19: Per-step ad-domain filter inputs ────────────────
    //
    // Comma-separated lists driving the four step-level domain
    // filters (skip_on_my_domain, skip_on_target, only_on_target,
    // only_on_my_domain) and the matching condition kinds. The runner
    // splits these on commas and seeds <c>RunContext.MyDomains</c> /
    // <c>TargetDomains</c> before each script run.
    //
    // Format: "example.com, www.example.com, partners.example.com"
    // Blank entries are dropped. Without a value the filters become
    // no-ops — same as before this field existed.

    /// <summary>Domains the profile owns (won't self-click ads on).</summary>
    public string? MyDomainsCsv { get; init; }

    /// <summary>Domains the profile is paid to drive traffic to.</summary>
    public string? TargetDomainsCsv { get; init; }
}

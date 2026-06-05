// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

using GhostShell.Core.Models;

namespace GhostShell.Runtime.Fingerprint;

/// <summary>
/// Feature #5 — per-profile stealth score. Blends the static fingerprint
/// coherence (<see cref="CoherenceValidator"/>, computed offline from the
/// device template, no browser) with the optional live self-check
/// (<see cref="SelfCheckResult"/>: exit IP, WebRTC leak, runtime probes).
/// Pure + deterministic so it's fully unit-testable and can be shown in the UI
/// before a profile is ever launched (coherence-only), then sharpened once a
/// self-check has run.
/// </summary>
public static class StealthScore
{
    public static StealthReport Combine(FingerprintScore coherence, SelfCheckResult? runtime)
    {
        var coh = Math.Clamp(coherence.Overall, 0, 100);
        int? run = runtime is null ? null : Math.Clamp(runtime.Score, 0, 100);

        // 50/50 blend when a runtime check exists; coherence-only otherwise.
        var score = run.HasValue
            ? (int)Math.Round(coh * 0.5 + run.Value * 0.5)
            : coh;

        var issues = new List<string>();
        if (coherence.CriticalIssues > 0) issues.Add($"{coherence.CriticalIssues} critical coherence issue(s)");
        if (coherence.Warnings > 0)       issues.Add($"{coherence.Warnings} coherence warning(s)");
        if (runtime?.WebRtcLeaked == true) issues.Add("WebRTC IP leak detected at runtime");
        if (run is < 60)                   issues.Add($"runtime self-check low ({run})");

        var band = score >= 85 ? StealthBand.Strong
                 : score >= 60 ? StealthBand.Fair
                 : StealthBand.Weak;

        // A live WebRTC leak deanonymises the user regardless of how clean the
        // fingerprint is — never call such a profile "Strong".
        if (runtime?.WebRtcLeaked == true && band == StealthBand.Strong)
            band = StealthBand.Fair;

        var summary = run.HasValue
            ? $"{band} — coherence {coh}, runtime {run}"
            : $"{band} — coherence {coh} (not runtime-verified; run a self-check)";

        return new StealthReport(score, band, coh, run, issues, summary);
    }
}

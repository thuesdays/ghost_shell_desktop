// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Common;

/// <summary>
/// Phase 30 — composes the URL pattern list for Chrome's
/// <c>Network.setBlockedURLs</c> CDP call from the user's blocking
/// toggles + custom patterns. Mirrors the legacy web project's
/// <c>_BLOCKLIST_BUCKETS</c> + <c>_build_blocked_url_patterns</c>
/// (ghost_shell/browser/runtime.py).
///
/// Patterns use Chrome DevTools wildcard syntax — <c>*</c> matches
/// any characters, no anchoring. None of these touch <c>google.com</c>
/// SERP HTML or core JS, so ad-detection still works on the SERP
/// (the parser reads the raw HTML before render).
/// </summary>
public static class ResourceBlockingPatterns
{
    public sealed record Bucket(string Key, string Label, IReadOnlyList<string> Patterns);

    public static IReadOnlyList<Bucket> AllBuckets { get; } = new[]
    {
        new Bucket("block_youtube_video", "YouTube video & thumbnails", new[]
        {
            "*://*.ytimg.com/*",
            "*://*.youtube.com/*.mp4*",
            "*://*.youtube.com/*.webm*",
            "*://*.googlevideo.com/*",
        }),
        new Bucket("block_google_images", "Google image thumbnails", new[]
        {
            "*://encrypted-tbn*.gstatic.com/*",
            "*://*.ggpht.com/*",
            "*://lh3.googleusercontent.com/*",
        }),
        new Bucket("block_google_maps_tiles", "Google Maps tiles", new[]
        {
            "*://mt0.google.com/vt/*",
            "*://mt1.google.com/vt/*",
            "*://mt2.google.com/vt/*",
            "*://mt3.google.com/vt/*",
            "*://maps.googleapis.com/maps/api/staticmap*",
        }),
        new Bucket("block_fonts", "Web fonts", new[]
        {
            "*://fonts.gstatic.com/*",
            "*.woff2",
            "*.woff",
        }),
        new Bucket("block_analytics", "Analytics & tracking beacons", new[]
        {
            "*://*.google-analytics.com/*",
            "*://*.googletagmanager.com/*",
            "*://*.doubleclick.net/pagead/*",
            "*://stats.g.doubleclick.net/*",
            "*://www.googleadservices.com/pagead/conversion/*",
        }),
        new Bucket("block_social_widgets", "Social widgets", new[]
        {
            "*://*.facebook.net/*",
            "*://*.facebook.com/plugins/*",
            "*://platform.twitter.com/*",
            "*://*.x.com/i/widgets/*",
            "*://*.linkedin.com/embed/*",
        }),
        new Bucket("block_video_everywhere", "All video files (any site)", new[]
        {
            "*.mp4",
            "*.webm",
            "*.m3u8",
            "*.ts",
            "*.ogv",
        }),
    };

    /// <summary>
    /// Compose the full pattern list given a <paramref name="enabledKeys"/>
    /// set + the user's <paramref name="customPatterns"/> blob (newline-
    /// separated). Custom patterns are split, trimmed, dedup'd, and
    /// appended after the bucket patterns, preserving insertion order.
    /// </summary>
    public static IReadOnlyList<string> Compose(
        IReadOnlySet<string> enabledKeys,
        string? customPatterns)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var bucket in AllBuckets)
        {
            if (!enabledKeys.Contains(bucket.Key)) continue;
            foreach (var p in bucket.Patterns)
                if (seen.Add(p)) result.Add(p);
        }
        foreach (var p in ParseCustomPatterns(customPatterns))
            if (seen.Add(p)) result.Add(p);
        return result;
    }

    /// <summary>Split the textarea blob into trimmed, non-empty
    /// patterns. Lines starting with <c>#</c> are full-line comments.
    /// Inline trailing <c>#</c> annotations are recognised ONLY when
    /// the hash is preceded by whitespace, so URL fragments like
    /// <c>*://example.com/path#section</c> survive intact (audit fix).</summary>
    public static IEnumerable<string> ParseCustomPatterns(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) yield break;
        foreach (var line in blob.Split(new[] { '\r', '\n' }, StringSplitOptions.None))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("#")) continue;
            // Find a "  # comment" or "\t# comment" suffix without
            // touching URL fragments. Walk char-by-char so we don't
            // mis-strip "path#frag" anywhere in the line.
            for (int i = 1; i < trimmed.Length; i++)
            {
                if (trimmed[i] == '#' && char.IsWhiteSpace(trimmed[i - 1]))
                {
                    trimmed = trimmed[..i].TrimEnd();
                    break;
                }
            }
            if (trimmed.Length == 0) continue;
            yield return trimmed;
        }
    }
}

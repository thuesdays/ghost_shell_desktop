// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>

namespace GhostShell.Core.Models;

/// <summary>
/// Phase 23 — built-in script templates. Shipped with the app, not
/// stored in the DB. The Scripts page surfaces them via a gallery
/// dialog; picking one creates a fresh <see cref="Script"/> seeded
/// with the template's name + description + StepsJson, then opens
/// the editor for the user to customise (selectors, credentials,
/// delays).
///
/// Templates are list-mode (sequential steps) for simplicity — users
/// can flip to graph mode after instantiation.
/// </summary>
public sealed record ScriptTemplate(
    string Id,
    string Name,
    string Category,
    string Icon,
    string Description,
    string StepsJson);

/// <summary>
/// Static catalog of templates. Each entry has hand-written JSON
/// modelling a realistic flow on the named site. Selectors are
/// indicative — sites change, users will need to tweak. Credentials
/// are placeholders (<c>{{username}}</c> / <c>{{password}}</c>) so
/// the user can wire them via <c>save_var</c> or vault later.
/// </summary>
public static class ScriptTemplateCatalog
{
    public static IReadOnlyList<ScriptTemplate> All => _all;

    /// <summary>Distinct categories in the catalog, in the order they
    /// should appear in the picker UI.</summary>
    public static IReadOnlyList<string> Categories =>
        new[] { "Auth", "Social", "Search", "Shopping", "News", "Utility" };

    private static readonly ScriptTemplate[] _all =
    {
        // ─── AUTH ─────────────────────────────────────────────────
        new("auth.gmail",
            "Gmail / Google login",
            "Auth", "🔐",
            "Open accounts.google.com, enter email + password, land in Gmail. " +
            "Replace {{username}} / {{password}} with save_var entries or your vault. " +
            "Each typed field is preceded by wait_for_selector so the runner doesn't " +
            "hit the page before the form mounts.",
            """
            [
              {"type":"navigate","params":{"url":"https://accounts.google.com/signin"}},
              {"type":"wait_for_selector","params":{"selector":"input#identifierId, input[type='email']","timeout_ms":20000}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":2200}},
              {"type":"type","params":{"selector":"input#identifierId, input[type='email']","text":"{{username}}","min_ms":60,"max_ms":180}},
              {"type":"dwell","params":{"min_ms":400,"max_ms":900}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_selector","params":{"selector":"input[type='password'][name='Passwd'], input[type='password']","timeout_ms":20000}},
              {"type":"dwell","params":{"min_ms":1000,"max_ms":2500}},
              {"type":"type","params":{"selector":"input[type='password'][name='Passwd'], input[type='password']","text":"{{password}}","min_ms":60,"max_ms":180}},
              {"type":"dwell","params":{"min_ms":400,"max_ms":900}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_url","params":{"pattern":"myaccount\\\\.google\\\\.com|mail\\\\.google\\\\.com","timeout_ms":25000}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}}
            ]
            """),

        new("auth.facebook",
            "Facebook login",
            "Auth", "🔐",
            "Sign in to facebook.com via the email + password form on the home screen.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.facebook.com/"}},
              {"type":"wait_for_selector","params":{"selector":"input#email","timeout_ms":20000}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":2200}},
              {"type":"type","params":{"selector":"input#email","text":"{{username}}","min_ms":50,"max_ms":160}},
              {"type":"dwell","params":{"min_ms":400,"max_ms":900}},
              {"type":"type","params":{"selector":"input#pass","text":"{{password}}","min_ms":50,"max_ms":160}},
              {"type":"click_selector","params":{"selector":"button[name='login']"}},
              {"type":"wait_for_url","params":{"pattern":"facebook\\\\.com\\\\/(?!login)","timeout_ms":25000}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}}
            ]
            """),

        new("auth.twitter",
            "X / Twitter login",
            "Auth", "🔐",
            "Sign in to x.com — flow has two screens: handle, then password. " +
            "Captures both with realistic typing.",
            """
            [
              {"type":"navigate","params":{"url":"https://x.com/i/flow/login"}},
              {"type":"wait_for_selector","params":{"selector":"input[autocomplete='username']","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":1200,"max_ms":2800}},
              {"type":"type","params":{"selector":"input[autocomplete='username']","text":"{{username}}","min_ms":60,"max_ms":170}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_selector","params":{"selector":"input[autocomplete='current-password']","timeout_ms":15000}},
              {"type":"type","params":{"selector":"input[autocomplete='current-password']","text":"{{password}}","min_ms":60,"max_ms":170}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_url","params":{"pattern":"x\\\\.com\\\\/home","timeout_ms":20000}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}}
            ]
            """),

        new("auth.linkedin",
            "LinkedIn login",
            "Auth", "🔐",
            "Sign in to linkedin.com via the email + password form on the home page.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.linkedin.com/login"}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"type","params":{"selector":"input#username","text":"{{username}}","min_ms":50,"max_ms":160}},
              {"type":"dwell","params":{"min_ms":400,"max_ms":900}},
              {"type":"type","params":{"selector":"input#password","text":"{{password}}","min_ms":50,"max_ms":160}},
              {"type":"click_selector","params":{"selector":"button[type='submit']"}},
              {"type":"wait_for_url","params":{"pattern":"linkedin\\\\.com\\\\/feed","timeout_ms":20000}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}}
            ]
            """),

        new("auth.reddit",
            "Reddit login",
            "Auth", "🔐",
            "Sign in to reddit.com. Uses the new domain — for old.reddit.com adjust selectors.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.reddit.com/login"}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"type","params":{"selector":"input[name='username']","text":"{{username}}","min_ms":60,"max_ms":170}},
              {"type":"dwell","params":{"min_ms":300,"max_ms":700}},
              {"type":"type","params":{"selector":"input[name='password']","text":"{{password}}","min_ms":60,"max_ms":170}},
              {"type":"click_selector","params":{"selector":"button[type='submit']"}},
              {"type":"wait_for_url","params":{"pattern":"reddit\\\\.com\\\\/(?!login)","timeout_ms":20000}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}}
            ]
            """),

        // ─── SOCIAL ───────────────────────────────────────────────
        new("social.instagram_warmup",
            "Instagram feed warmup",
            "Social", "📸",
            "Open instagram.com (assumes already logged in), scroll the feed " +
            "for ~30s, like 1–2 posts, then idle.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.instagram.com/"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}},
              {"type":"scroll","params":{"seconds":12}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3500}},
              {"type":"click_selector","params":{"selector":"section svg[aria-label='Like']"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}},
              {"type":"scroll","params":{"seconds":10}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}}
            ]
            """),

        new("social.x_timeline_scroll",
            "X / Twitter timeline scroll",
            "Social", "🐦",
            "Browse x.com home feed for ~30s, hover a couple of posts to " +
            "look engaged. Assumes already logged in.",
            """
            [
              {"type":"navigate","params":{"url":"https://x.com/home"}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5500}},
              {"type":"scroll","params":{"seconds":10}},
              {"type":"hover","params":{"selector":"article"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4000}},
              {"type":"scroll","params":{"seconds":8}},
              {"type":"hover","params":{"selector":"article"}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3500}},
              {"type":"scroll","params":{"seconds":10}}
            ]
            """),

        new("social.facebook_newsfeed",
            "Facebook newsfeed scroll",
            "Social", "📘",
            "Open facebook.com home, scroll the feed, hover stories, idle. " +
            "No interactions to keep the warmup low-risk.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.facebook.com/"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6500}},
              {"type":"scroll","params":{"seconds":12}},
              {"type":"hover","params":{"selector":"div[role='article']"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}},
              {"type":"scroll","params":{"seconds":10}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}}
            ]
            """),

        new("social.youtube_watch",
            "YouTube watch + scroll comments",
            "Social", "▶️",
            "Open youtube.com, click a homepage video thumbnail, watch ~45s, " +
            "scroll comments, then return home.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.youtube.com/"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}},
              {"type":"click_selector","params":{"selector":"a#thumbnail"}},
              {"type":"wait_for_url","params":{"pattern":"watch\\\\?v=","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":4000,"max_ms":8000}},
              {"type":"scroll","params":{"seconds":12}},
              {"type":"hover","params":{"selector":"#comments"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4000}},
              {"type":"scroll","params":{"seconds":15}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}}
            ]
            """),

        // ─── SEARCH ───────────────────────────────────────────────
        new("search.google",
            "Google search query",
            "Search", "🔍",
            "Open google.com, type a query, press Enter, dwell on the SERP. " +
            "Replace {{query}} with save_var or hard-code.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.google.com/"}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3500}},
              {"type":"type","params":{"selector":"textarea[name='q']","text":"{{query}}","min_ms":50,"max_ms":160}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_url","params":{"pattern":"google\\\\.com\\\\/search","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}},
              {"type":"scroll","params":{"seconds":8}}
            ]
            """),

        new("search.bing",
            "Bing search query",
            "Search", "🔍",
            "Open bing.com, type a query, dwell on results.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.bing.com/"}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"type","params":{"selector":"input#sb_form_q","text":"{{query}}","min_ms":50,"max_ms":160}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_url","params":{"pattern":"bing\\\\.com\\\\/search","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5500}},
              {"type":"scroll","params":{"seconds":7}}
            ]
            """),

        new("search.duckduckgo",
            "DuckDuckGo search query",
            "Search", "🔍",
            "Open duckduckgo.com, type a query, dwell on results.",
            """
            [
              {"type":"navigate","params":{"url":"https://duckduckgo.com/"}},
              {"type":"dwell","params":{"min_ms":1200,"max_ms":2800}},
              {"type":"type","params":{"selector":"input#search_form_input_homepage, input[name='q']","text":"{{query}}","min_ms":50,"max_ms":160}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_selector","params":{"selector":"article[data-testid='result']","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}},
              {"type":"scroll","params":{"seconds":6}}
            ]
            """),

        // ─── SHOPPING ─────────────────────────────────────────────
        new("shop.amazon_browse",
            "Amazon — search + browse",
            "Shopping", "🛒",
            "Search amazon.com for {{query}}, browse results, click first item, " +
            "scroll product page.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.amazon.com/"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}},
              {"type":"type","params":{"selector":"input#twotabsearchtextbox","text":"{{query}}","min_ms":60,"max_ms":170}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_url","params":{"pattern":"amazon\\\\.com\\\\/s","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}},
              {"type":"scroll","params":{"seconds":7}},
              {"type":"click_selector","params":{"selector":"div[data-component-type='s-search-result'] h2 a"}},
              {"type":"wait_for_url","params":{"pattern":"\\\\/dp\\\\/","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}},
              {"type":"scroll","params":{"seconds":10}}
            ]
            """),

        new("shop.ebay_browse",
            "eBay — search + browse",
            "Shopping", "🛒",
            "Search ebay.com for {{query}}, browse listings, hover a few, " +
            "click into one.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.ebay.com/"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}},
              {"type":"type","params":{"selector":"input#gh-ac","text":"{{query}}","min_ms":60,"max_ms":170}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"wait_for_url","params":{"pattern":"ebay\\\\.com\\\\/sch","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}},
              {"type":"scroll","params":{"seconds":7}},
              {"type":"hover","params":{"selector":"li.s-item"}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"click_selector","params":{"selector":"li.s-item a.s-item__link"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}},
              {"type":"scroll","params":{"seconds":8}}
            ]
            """),

        new("shop.aliexpress_browse",
            "AliExpress — search + browse",
            "Shopping", "🛒",
            "Search aliexpress.com for {{query}}, scroll results, open one product.",
            """
            [
              {"type":"navigate","params":{"url":"https://www.aliexpress.com/"}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}},
              {"type":"type","params":{"selector":"input[name='SearchText']","text":"{{query}}","min_ms":60,"max_ms":170}},
              {"type":"press_key","params":{"key":"Enter"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}},
              {"type":"scroll","params":{"seconds":10}},
              {"type":"click_selector","params":{"selector":"a[href*='/item/']"}},
              {"type":"dwell","params":{"min_ms":3500,"max_ms":7000}},
              {"type":"scroll","params":{"seconds":10}}
            ]
            """),

        // ─── NEWS ─────────────────────────────────────────────────
        new("news.cnn",
            "CNN — read homepage",
            "News", "📰",
            "Open cnn.com, scroll the homepage, click into a top story, read.",
            """
            [
              {"type":"navigate","params":{"url":"https://edition.cnn.com/"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}},
              {"type":"scroll","params":{"seconds":10}},
              {"type":"click_selector","params":{"selector":"a.container__link"}},
              {"type":"dwell","params":{"min_ms":3500,"max_ms":7000}},
              {"type":"scroll","params":{"seconds":15}}
            ]
            """),

        new("news.wikipedia_random",
            "Wikipedia — random article",
            "News", "📚",
            "Open Special:Random on Wikipedia, scroll the article, follow a hyperlink.",
            """
            [
              {"type":"navigate","params":{"url":"https://en.wikipedia.org/wiki/Special:Random"}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5500}},
              {"type":"scroll","params":{"seconds":12}},
              {"type":"click_selector","params":{"selector":"#bodyContent a[href^='/wiki/']:not([href*=':'])"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}},
              {"type":"scroll","params":{"seconds":10}}
            ]
            """),

        // ─── UTILITY ──────────────────────────────────────────────
        new("util.csv_foreach",
            "CSV foreach — drive runs from a file",
            "Utility", "📋",
            "Read a CSV column from %LocalAppData%\\GhostShell\\csv-data\\, iterate " +
            "every row, navigate to each URL, dwell. Replace targets.csv + 'url' column.",
            """
            [
              {"type":"foreach","params":{"source":"csv_file","csv_path":"targets.csv","csv_column":"url","csv_has_header":true,"var":"row_url"},
               "body":[
                  {"type":"navigate","params":{"url":"{{row_url}}"}},
                  {"type":"dwell","params":{"min_ms":3500,"max_ms":7500}},
                  {"type":"scroll","params":{"seconds":6}},
                  {"type":"random_delay","params":{"min_ms":1500,"max_ms":4000}}
               ]
              }
            ]
            """),

        new("util.captcha_aware_visit",
            "Captcha-aware visit",
            "Utility", "🛡",
            "Navigate to {{url}}, detect + solve a captcha if present (via 2captcha), " +
            "then dwell. Use this when targets sometimes throw a challenge.",
            """
            [
              {"type":"navigate","params":{"url":"{{url}}"}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5000}},
              {"type":"solve_captcha","params":{"timeout_sec":180}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":5500}},
              {"type":"scroll","params":{"seconds":8}}
            ]
            """),

        new("util.webhook_ping",
            "Webhook ping after browse",
            "Utility", "🌐",
            "Navigate to a target, extract a value, POST it to your webhook so " +
            "your back-end knows the run completed. SSRF-guarded.",
            """
            [
              {"type":"navigate","params":{"url":"{{url}}"}},
              {"type":"dwell","params":{"min_ms":2000,"max_ms":4500}},
              {"type":"extract_text","params":{"selector":"title","save_as":"page_title"}},
              {"type":"http_request","params":{"method":"POST","url":"https://example.com/webhook","body":"{\"profile\":\"{{ad_title}}\",\"title\":\"{{page_title}}\"}","timeout_sec":15}}
            ]
            """),
    };
}

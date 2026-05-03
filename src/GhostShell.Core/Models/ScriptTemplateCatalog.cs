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
        new[] { "Auth", "Wallet", "Social", "Search", "Shopping", "News", "Utility" };

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

        // ─── WALLET ───────────────────────────────────────────────
        // Phase 67 — bulk wallet import scripts. The pattern: open the
        // extension's onboarding page, click "Import existing wallet",
        // paste the seed phrase / private key from the user's vault,
        // set a password, and confirm. Run on 100+ profiles in parallel
        // via Bulk Start (Phase 64) to provision a farm in minutes.
        //
        // SAFETY: seed phrases / private keys MUST live in the Vault
        // (Phase 24-26). Templates reference them via {{vault.SEED}}
        // — the runner expands at execution time, never logs the
        // expanded value, and clears the in-memory bag on script
        // completion. NEVER hardcode a key into a script JSON.
        //
        // Selectors below are best-guess for the wallet's current
        // onboarding UI as of late 2025. Wallets ship UI changes
        // weekly — if the template breaks, open the script in the
        // visual editor and adjust selectors via the recorder.

        new("wallet.okx.seed",
            "OKX Wallet — import by seed phrase",
            "Wallet", "🔐",
            "Import an existing wallet into the OKX extension via 12/24-word seed " +
            "phrase. Vault refs: SEED (seed phrase) + PASS (new local password). " +
            "OKX usually auto-opens its onboarding tab on first launch — the " +
            "template waits for that, then walks the import flow. " +
            "Build a profile farm by Bulk Start with this script assigned.",
            """
            [
              {"type":"wait_for_url","params":{"pattern":"chrome-extension://.*okx|okxweb3","timeout_ms":30000}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"click_selector","params":{"selector":"button:contains('Import wallet'), [data-testid='import-wallet'], a[href*='import']","fallback_text":"Import wallet"}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":1800}},
              {"type":"click_selector","params":{"selector":"button:contains('Seed phrase'), [data-testid='seed-phrase'], button[data-import-type='seed']","fallback_text":"Seed phrase"}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":1800}},
              {"type":"type","params":{"selector":"textarea, input[type='password'][placeholder*='phrase' i], [contenteditable='true']","text":"{{vault.SEED}}","min_ms":40,"max_ms":120}},
              {"type":"dwell","params":{"min_ms":600,"max_ms":1400}},
              {"type":"click_selector","params":{"selector":"button:contains('Confirm'), button:contains('Continue'), button[type='submit']","fallback_text":"Confirm"}},
              {"type":"wait_for_selector","params":{"selector":"input[type='password'][name*='pass' i], input[placeholder*='password' i]","timeout_ms":15000}},
              {"type":"type","params":{"selector":"input[type='password'][name*='pass' i]:nth-of-type(1), input[type='password']:nth-of-type(1)","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"dwell","params":{"min_ms":300,"max_ms":700}},
              {"type":"type","params":{"selector":"input[type='password']:nth-of-type(2), input[placeholder*='confirm' i]","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"dwell","params":{"min_ms":300,"max_ms":700}},
              {"type":"click_selector","params":{"selector":"input[type='checkbox'], [role='checkbox']"}},
              {"type":"dwell","params":{"min_ms":400,"max_ms":900}},
              {"type":"click_selector","params":{"selector":"button[type='submit'], button:contains('Confirm'), button:contains('Continue')","fallback_text":"Confirm"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}}
            ]
            """),

        new("wallet.okx.privkey",
            "OKX Wallet — import by private key",
            "Wallet", "🔑",
            "Same as the seed-phrase variant but uses a single hex private key. " +
            "Vault refs: PRIVKEY (hex without 0x or with — OKX accepts both) + " +
            "PASS (local password).",
            """
            [
              {"type":"wait_for_url","params":{"pattern":"chrome-extension://.*okx|okxweb3","timeout_ms":30000}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"click_selector","params":{"selector":"button:contains('Import wallet'), [data-testid='import-wallet']","fallback_text":"Import wallet"}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":1800}},
              {"type":"click_selector","params":{"selector":"button:contains('Private key'), [data-testid='private-key'], button[data-import-type='privkey']","fallback_text":"Private key"}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":1800}},
              {"type":"type","params":{"selector":"input[type='password'][placeholder*='private' i], textarea[placeholder*='private' i]","text":"{{vault.PRIVKEY}}","min_ms":40,"max_ms":120}},
              {"type":"dwell","params":{"min_ms":600,"max_ms":1400}},
              {"type":"click_selector","params":{"selector":"button:contains('Confirm'), button[type='submit']","fallback_text":"Confirm"}},
              {"type":"wait_for_selector","params":{"selector":"input[type='password'][name*='pass' i]","timeout_ms":15000}},
              {"type":"type","params":{"selector":"input[type='password']:nth-of-type(1)","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"dwell","params":{"min_ms":300,"max_ms":700}},
              {"type":"type","params":{"selector":"input[type='password']:nth-of-type(2)","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"dwell","params":{"min_ms":400,"max_ms":900}},
              {"type":"click_selector","params":{"selector":"input[type='checkbox']"}},
              {"type":"click_selector","params":{"selector":"button[type='submit'], button:contains('Confirm')","fallback_text":"Confirm"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}}
            ]
            """),

        new("wallet.metamask.seed",
            "MetaMask — import by seed phrase",
            "Wallet", "🦊",
            "Import an existing wallet into MetaMask via the 12/24-word recovery " +
            "phrase. Each word goes into a separate input — the script types them " +
            "space-separated and MetaMask auto-distributes via paste handler. " +
            "Vault refs: SEED (recovery phrase) + PASS (local password ≥ 8 chars).",
            """
            [
              {"type":"wait_for_url","params":{"pattern":"chrome-extension://.*onboarding|metamask","timeout_ms":30000}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"click_selector","params":{"selector":"input[type='checkbox'][data-testid='onboarding-terms-checkbox'], input[data-testid*='terms']"}},
              {"type":"click_selector","params":{"selector":"button[data-testid='onboarding-import-wallet'], button:contains('Import an existing wallet')","fallback_text":"Import an existing wallet"}},
              {"type":"dwell","params":{"min_ms":1000,"max_ms":2000}},
              {"type":"click_selector","params":{"selector":"button[data-testid='metametrics-no-thanks'], button:contains('No thanks')","fallback_text":"No thanks"}},
              {"type":"wait_for_selector","params":{"selector":"input[data-testid='import-srp__srp-word-0'], input[placeholder*='Recovery']","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":1500}},
              {"type":"type","params":{"selector":"input[data-testid='import-srp__srp-word-0']","text":"{{vault.SEED}}","min_ms":50,"max_ms":140}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"click_selector","params":{"selector":"button[data-testid='import-srp-confirm'], button:contains('Confirm Secret Recovery Phrase')","fallback_text":"Confirm Secret Recovery Phrase"}},
              {"type":"wait_for_selector","params":{"selector":"input[data-testid='create-password-new'], input[autocomplete='new-password']","timeout_ms":15000}},
              {"type":"type","params":{"selector":"input[data-testid='create-password-new'], input[autocomplete='new-password']:nth-of-type(1)","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"type","params":{"selector":"input[data-testid='create-password-confirm'], input[autocomplete='new-password']:nth-of-type(2)","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"click_selector","params":{"selector":"input[data-testid='create-password-terms'], input[type='checkbox']:nth-of-type(1)"}},
              {"type":"click_selector","params":{"selector":"button[data-testid='create-password-import'], button:contains('Import my wallet')","fallback_text":"Import my wallet"}},
              {"type":"dwell","params":{"min_ms":4000,"max_ms":7000}},
              {"type":"click_selector","params":{"selector":"button[data-testid='onboarding-complete-done'], button:contains('Got it')","fallback_text":"Got it"}}
            ]
            """),

        new("wallet.metamask.privkey",
            "MetaMask — import by private key (existing wallet)",
            "Wallet", "🔑",
            "Adds a private-key account to a MetaMask instance that's ALREADY " +
            "set up (it doesn't bootstrap a fresh extension). Run after the " +
            "seed-import template has finished, or on profiles where MetaMask " +
            "is unlocked. Vault ref: PRIVKEY.",
            """
            [
              {"type":"wait_for_url","params":{"pattern":"chrome-extension://.*home\\\\.html|metamask","timeout_ms":30000}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"click_selector","params":{"selector":"button[data-testid='account-menu-icon'], [aria-label='Account menu']"}},
              {"type":"dwell","params":{"min_ms":600,"max_ms":1400}},
              {"type":"click_selector","params":{"selector":"button[data-testid='multichain-account-menu-popover-action-button'], button:contains('Add account or hardware wallet')","fallback_text":"Add account or hardware wallet"}},
              {"type":"dwell","params":{"min_ms":600,"max_ms":1400}},
              {"type":"click_selector","params":{"selector":"button[data-testid='multichain-account-menu-popover-add-imported-account'], button:contains('Import account')","fallback_text":"Import account"}},
              {"type":"wait_for_selector","params":{"selector":"input#private-key-box, input[id*='private']","timeout_ms":15000}},
              {"type":"type","params":{"selector":"input#private-key-box, input[id*='private']","text":"{{vault.PRIVKEY}}","min_ms":50,"max_ms":140}},
              {"type":"dwell","params":{"min_ms":500,"max_ms":1100}},
              {"type":"click_selector","params":{"selector":"button[data-testid='import-account-confirm-button'], button:contains('Import')","fallback_text":"Import"}},
              {"type":"dwell","params":{"min_ms":2500,"max_ms":4500}}
            ]
            """),

        new("wallet.phantom.seed",
            "Phantom (Solana) — import by seed phrase",
            "Wallet", "👻",
            "Import an existing Solana wallet into Phantom via the 12/24-word seed. " +
            "Vault refs: SEED + PASS. Phantom's onboarding auto-opens on first " +
            "extension launch.",
            """
            [
              {"type":"wait_for_url","params":{"pattern":"chrome-extension://.*phantom|onboarding\\\\.html","timeout_ms":30000}},
              {"type":"dwell","params":{"min_ms":1500,"max_ms":3000}},
              {"type":"click_selector","params":{"selector":"button:contains('I already have a wallet'), button[data-id='already-have-wallet']","fallback_text":"I already have a wallet"}},
              {"type":"dwell","params":{"min_ms":800,"max_ms":1800}},
              {"type":"click_selector","params":{"selector":"button:contains('Import Secret Recovery Phrase'), button[data-id='secret-phrase']","fallback_text":"Import Secret Recovery Phrase"}},
              {"type":"wait_for_selector","params":{"selector":"input[name='word-0'], textarea[placeholder*='phrase' i]","timeout_ms":15000}},
              {"type":"dwell","params":{"min_ms":600,"max_ms":1400}},
              {"type":"type","params":{"selector":"input[name='word-0'], textarea[placeholder*='phrase' i]","text":"{{vault.SEED}}","min_ms":50,"max_ms":140}},
              {"type":"dwell","params":{"min_ms":1000,"max_ms":2000}},
              {"type":"click_selector","params":{"selector":"button[type='submit'], button:contains('Continue'), button:contains('Import')","fallback_text":"Continue"}},
              {"type":"wait_for_selector","params":{"selector":"input[type='password'][name='password']","timeout_ms":15000}},
              {"type":"type","params":{"selector":"input[type='password'][name='password']","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"type","params":{"selector":"input[type='password'][name='confirmPassword'], input[type='password']:nth-of-type(2)","text":"{{vault.PASS}}","min_ms":50,"max_ms":140}},
              {"type":"click_selector","params":{"selector":"input[type='checkbox']"}},
              {"type":"click_selector","params":{"selector":"button[type='submit']:contains('Continue'), button[type='submit']","fallback_text":"Continue"}},
              {"type":"dwell","params":{"min_ms":3000,"max_ms":6000}}
            ]
            """),
    };
}

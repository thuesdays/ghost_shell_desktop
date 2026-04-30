# Ghost Shell Web Runner - Complete Action Type Inventory

**Audit Date:** April 2026  
**Web Version Source:** `F:\projects\ghost_shell_browser`  
**Purpose:** Full parameter schema for all action types to validate desktop port coverage

---

## EXECUTIVE SUMMARY

The web runner supports **44 action types** across multiple scopes:
- **Legacy Pipeline Actions** (24 types): Click, navigate, interact, wait, extract, execute
- **Loop-Level Actions** (7 types): Search, rotate, pause, iterate, refresh
- **Unified Flow Actions** (13 types): Control flow, data ops, HTTP, extensions

All actions are defined in `runner.py` with two main dispatch tables:
- `ACTION_HANDLERS` (per-ad pipeline)
- `LOOP_ACTION_HANDLERS` (main script loop)
- Plus unified-flow handlers invoked directly from `_exec_single()`

---

## PART 1: PER-AD PIPELINE ACTIONS

These run inside `run_pipeline()` after each ad is found. They accept `(driver, action: dict, ctx: dict)`.

### Common Metadata for All Per-Ad Actions
- **enabled** (bool, default true): Skip this step
- **probability** (float 0-1, default 1.0): Chance to run (0.3 = 30%)
- **skip_on_my_domain** (bool, default false): Skip if ad domain matches `ctx.my_domains`
- **skip_on_target** (bool, default false): Skip if ad is on a target domain
- **only_on_target** (bool, default false): Run ONLY for target-domain ads
- **only_on_my_domain** (bool, default false): Run ONLY for my-domain ads
- **abort_on_error** (bool, default false): Propagate exceptions, halt pipeline

---

### 1. **click_ad**
**Category:** ads  
**Scope:** per_ad  
**Description:** Click the exact ad anchor marked by `parse_ads`. Uses primary lookup (stamped `data-gs-ad-id`), then URL fragment fallback, then JavaScript domain-match scan. Ctrl+Click opens in new tab by default.

**Parameters:**
- `dwell_min` (number, default 6 sec): Min dwell after click before scroll/close
- `dwell_max` (number, default 18 sec): Max dwell
- `scroll_after_click` (bool, default true): Scroll the page after landing
- `close_after` (bool, default true): Close the tab when done
- `deep_dive` (bool, default false): After landing, click 1-2 internal links
- `depth_min` (number, default 1): Min internal clicks
- `depth_max` (number, default 2): Max internal clicks
- `inner_dwell_min` (number, default 5 sec): Min dwell per internal page
- `inner_dwell_max` (number, default 12 sec): Max dwell per internal page

**Side Effects:**
- Logs: `action_events.action_type_log` → outcome = "ran" / "error"
- Closes opened tabs when `close_after=true`
- Records dwell duration

---

### 2. **click_selector**
**Category:** interaction  
**Scope:** per_ad  
**Description:** Click any element by CSS selector with human mouse movement. Uses persona-based pre-click dwell.

**Parameters:**
- `selector` (string, required): CSS selector
- `new_tab` (bool, default false): Ctrl+Click instead of regular click

**Side Effects:**
- Mouse moves along curve to element
- Persona-shaped hover pause before clicking

---

### 3. **visit**
**Category:** navigation  
**Scope:** per_ad  
**Description:** Navigate directly to a URL (from action param or ad's click URL). Own-domain guard blocks navigation if URL matches `my_domains`.

**Parameters:**
- `url` (string): Defaults to `ad.google_click_url` or `ad.clean_url`
- `new_tab` (bool, default true): Open in new tab
- `dwell_min` (number, default 5 sec)
- `dwell_max` (number, default 15 sec)
- `close_after` (bool, default true): Close new tabs

**Side Effects:**
- Closes any opened tabs on error/finish
- Checks "landed on own domain" and closes tab if true
- Records dwell

---

### 4. **hover**
**Category:** interaction  
**Description:** Move mouse to selector and hold.

**Parameters:**
- `selector` (string, required): CSS selector
- `hold_min` (number, default 0.5 sec)
- `hold_max` (number, default 2.0 sec)

---

### 5. **move_random**
**Category:** interaction  
**Description:** Move mouse to random viewport coordinates. Creates invisible div, moves to its center, deletes it.

**Parameters:** None

---

### 6. **scroll**
**Category:** interaction  
**Description:** Human-like scroll with occasional back-scrolls.

**Parameters:**
- `min_px` (number, default 300): Min scroll distance
- `max_px` (number, default 900): Max scroll distance
- `backtracking` (bool, default true): Occasionally scroll back up

---

### 7. **read**
**Category:** interaction  
**Description:** Simulates reading: scroll chunk → pause 2-5s → repeat 3-6 times.

**Parameters:**
- `min_paragraphs` (number, default 3)
- `max_paragraphs` (number, default 6)
- `pause_min` (number, default 2.0 sec)
- `pause_max` (number, default 5.5 sec)

---

### 8. **type**
**Category:** interaction  
**Description:** Char-by-char typing with realistic per-key delay. Timing derives from profile's `typing_wpm` persona dimension. Punctuation is 1.6× slower.

**Parameters:**
- `selector` (string, required): CSS selector of input field
- `text` (string, required): Text to type

**Side Effects:**
- Clicks element first, then types with per-char delay
- Persona-driven timing (fallback 25-60 ms per char)

---

### 9. **press_key**
**Category:** interaction  
**Description:** Send a single keyboard key (ENTER, ESCAPE, TAB, etc.).

**Parameters:**
- `key` (string, required): Key name (ENTER, ESCAPE, TAB, SPACE, BACKSPACE, ARROW_UP/DOWN/LEFT/RIGHT)

---

### 10. **dwell**
**Category:** timing  
**Description:** Sleep for random duration between min and max.

**Parameters:**
- `min_sec` (number, default 2): Min seconds
- `max_sec` (number, default 6): Max seconds

---

### 11. **random_delay**
**Category:** timing  
**Description:** Shortcut for dwell with preset sizes.

**Parameters:**
- `size` (string, default "medium"): tiny | small | medium | long
  - tiny: 0.3-1.0s
  - small: 1.0-3.0s
  - medium: 4.0-8.0s
  - long: 10.0-20.0s

---

### 12. **scroll_to_bottom**
**Category:** interaction  
**Description:** Gradually scroll to bottom of page with random delays between chunks.

**Parameters:** None

**Side Effects:**
- Polling loop up to 30 iterations, breaks early if no movement

---

### 13. **back**
**Category:** navigation  
**Description:** Browser back button (driver.back()).

**Parameters:**
- `delay_sec` (number, default 1): Sleep before going back

---

### 14. **new_tab**
**Category:** navigation  
**Description:** Open new tab (about:blank) and switch to it.

**Parameters:** None

---

### 15. **close_tab**
**Category:** navigation  
**Description:** Close current tab, switch to first.

**Parameters:** None

---

### 16. **switch_tab**
**Category:** navigation  
**Description:** Switch to tab by index.

**Parameters:**
- `index` (number, default 0): Tab index (0-based)

---

### 17. **wait_for**
**Category:** timing  
**Scope:** per_ad  
**Description:** Block until selector appears (WebDriverWait with presence condition).

**Parameters:**
- `selector` (string, required): CSS selector
- `timeout_sec` (number, default 10): Timeout in seconds
  - Also reads legacy `timeout` param (same field, renamed)

---

### 18. **open_url**
**Category:** navigation  
**Scope:** per_ad  
**Description:** Navigate with variable substitution. Alias of `visit` for non-ads scripts.

**Parameters:**
- `url` (string, required): URL template (supports {var} substitution)
- `dwell_min` (number, default 4 sec)
- `dwell_max` (number, default 12 sec)
- `wait_after` (number, default 1.0 sec): Used if no dwell_min/max
- `scroll` (bool, default false): Human scroll while dwelling
- `scroll_steps` (number, default 4): Number of scroll steps

**Side Effects:**
- `{var}` placeholders interpolated via `_subst()`
- Saves last_screenshot_path if screenshot taken

---

### 19. **fill_form**
**Category:** interaction  
**Scope:** per_ad  
**Description:** Type into one or more form fields with realistic keystroke timing.

**Parameters:**
- `selector` (string): CSS selector for single field
- `value` (string): Text to type (supports {var} substitution)
- `clear_first` (bool, default true): Clear field before typing
- `fields` (JSON array, alternative): Multi-field mode
  - Each: `{selector: "...", value: "...", clear_first?: bool}`

**Side Effects:**
- Small per-char delay (0.03-0.12 sec)
- 0.15-0.45 sec pause after each field

---

### 20. **extract_text**
**Category:** data  
**Scope:** per_ad  
**Description:** Pull element's text or attribute, store in `ctx.vars`.

**Parameters:**
- `selector` (string, required): CSS selector
- `attribute` (string): Element attribute (omit for .text)
- `store_as` (string, default "last_extract"): Variable name

**Side Effects:**
- Stores in `ctx.vars[store_as]`
- Stores `last_extract` in context vars

---

### 21. **execute_js**
**Category:** power  
**Scope:** per_ad  
**Description:** Run arbitrary JavaScript in page context. Supports {var} substitution in code.

**Parameters:**
- `code` (string, required): JS code (function body, use `return`)
- `store_as` (string): Variable name for return value

**Side Effects:**
- Stores return value in `ctx.vars[store_as]`
- Power tool — anti-bot systems can fingerprint automated JS

---

### 22. **screenshot**
**Category:** data  
**Scope:** per_ad  
**Description:** Save PNG screenshot to `profiles/<name>/screenshots/`.

**Parameters:**
- `name` (string, default "shot"): Filename prefix (supports {var} substitution)
- Timestamp suffix added automatically: `shot_YYYYMMDD_HHMMSS.png`

**Side Effects:**
- Creates directory if missing
- Stores path in `ctx.vars["last_screenshot_path"]`

---

### 23. **wait_for_url**
**Category:** timing  
**Scope:** per_ad  
**Description:** Block until URL contains substring or matches regex (polls every 0.4s).

**Parameters:**
- `contains` (string): Substring to match (simpler)
- `regex` (string): Regex pattern (takes precedence)
- `timeout` (number, default 15 sec)

**Side Effects:**
- Watchdog shield applied for long waits (>25s)

---

### 24. **touch_click** (Mobile)
**Category:** interaction  
**Scope:** per_ad  
**Description:** Emulate single-finger tap via CDP `Input.dispatchTouchEvent`.

**Parameters:**
- `selector` (string): CSS selector (preferred)
- `x`, `y` (number): Fallback viewport coordinates
- `duration` (number, default 80 ms): Time between touchStart and touchEnd

**Side Effects:**
- Sends touchStart → (optional tiny dwell) → touchEnd

---

### 25. **swipe** (Mobile)
**Category:** interaction  
**Scope:** per_ad  
**Description:** Emulate swipe gesture (up/down/left/right).

**Parameters:**
- `direction` (string, default "up"): up | down | left | right
- `from_x`, `from_y` (number): Optional start point
- `to_x`, `to_y` (number): Optional end point
- `duration` (number, default 400 ms): Total swipe time
- `steps` (number, default 16): Number of intermediate touchMove events

**Side Effects:**
- Auto-derives direction endpoints if not specified

---

## PART 2: LOOP-LEVEL ACTIONS

These run in `run_main_script()` inside the query/profile loop. Signature: `(browser, step, loop_ctx)`.

**loop_ctx contract:**
```python
{
    "all_queries": list[str],           # Queries from DB
    "search_query": callable(q) → list,  # Run query, return ads
    "rotate_ip": callable() → str,       # Rotate proxy, return new IP
    "per_ad_runner": callable(ad, q),    # Run per-ad pipeline
    "watchdog": watchdog object,         # .heartbeat() / .pause()
}
```

### 26. **search_query**
**Scope:** loop  
**Description:** Search Google, parse SERP, optionally dispatch per-ad pipeline.

**Parameters:**
- `query` (string, required): Search term
- `fail_on_empty` (bool, default false): Raise error if 0 ads
- `refine_on_zero_ads` (bool, default true): Auto-retry with long-tail variants
- `refine_max_attempts` (number, default 3): Max long-tail retries
- `refine_locale` (string, default "UA"): Locale for suffixes (UA, RU, EN, combos)

**Side Effects:**
- Stores ads in context
- Sets `ctx.flags["ads_found"]`
- Auto-legacy: if step has `auto_foreach=true`, runs per_ad_runner for each ad

---

### 27. **search_all_queries**
**Scope:** loop  
**Description:** Iterate all queries from `db.search.queries`, search each, dispatch per-ad.

**Parameters:**
- `shuffle` (bool, default true): Randomize order

**Side Effects:**
- (Auto-migrated to `loop` with `items_from="queries"`)

---

### 28. **rotate_ip**
**Scope:** loop  
**Description:** Force proxy rotation. No-op on static proxies.

**Parameters:**
- `wait_after_sec` (number, default 4): Pause after rotation

---

### 29. **pause**
**Scope:** loop  
**Description:** Sleep for random duration (simulates human distraction).

**Parameters:**
- `min_sec` (number, default 3)
- `max_sec` (number, default 8)

---

### 30. **visit_url**
**Scope:** loop  
**Description:** Navigate to arbitrary URL, dwell, continue.

**Parameters:**
- `url` (string, required)
- `dwell_min` (number, default 4 sec)
- `dwell_max` (number, default 12 sec)

---

### 31. **refresh**
**Scope:** loop  
**Description:** Reload current page N times with delays. Useful after `search_query` returns 0 ads (retry the SERP).

**Parameters:**
- `max_attempts` (number, default 3)
- `delay_min_sec` (number, default 3)
- `delay_max_sec` (number, default 8)

**Side Effects:**
- Validates current URL is a Google SERP before refreshing
- Sets page_load_timeout to 15s (caps slow testers)

---

### 32. **loop** (aka foreach)
**Scope:** loop  
**Description:** Iterate a custom list of items, run nested steps once per item.

**Parameters:**
- `items` (list[str]): Explicit list OR string from `items_from`
- `items_from` (string): "queries" to pull from DB
- `item_var` (string, default "item"): Placeholder name for {item}
- `shuffle` (bool, default true)
- `steps` (list): Nested action steps

**Side Effects:**
- Substitutes `{item}` in nested step params
- Variable survives across iterations

---

## PART 3: UNIFIED FLOW ACTIONS

These are new (Phase 5, Apr 2026) and run via `_exec_single()` inside `RunContext`. Signature: `(step: dict, ctx: RunContext)`.

### Container Actions (have nested `steps`)

#### 33. **if**
**Scope:** any  
**Category:** flow  
**Description:** Conditional branching with then/else branches.

**Parameters:**
- `condition` (dict): Condition spec (see CONDITION_KINDS)
- `then_steps` (list): Steps to run when true
- `else_steps` (list): Steps to run when false (optional)

**Conditions Supported:**
- `always` — always run
- `ads_found` — ads were found
- `no_ads` — no ads found
- `ads_count_gte: {value}` — ads.count >= N
- `ad_is_competitor` — needs_ad, checks domain classification
- `ad_is_external` — needs_ad, not on my domain (includes targets)
- `ad_is_target` — needs_ad
- `ad_is_mine` — needs_ad
- `captcha_present` — page has captcha
- `url_contains: {value}` — substring in current URL
- `element_exists: {selector}` — CSS selector exists
- `var_equals: {var, value}` — variable == literal
- `var_contains: {var, value}` — variable contains substring
- `var_matches: {var, value}` — variable matches regex
- `var_empty: {var}` — variable is empty

**Side Effects:**
- Context snapshot/restore on branch

---

#### 34. **foreach_ad**
**Scope:** any  
**Category:** flow  
**Description:** Iterate ads from most recent search/catch, run nested steps once per ad.

**Parameters:**
- `shuffle` (bool, default false): Randomize ad order
- `limit` (number): Cap on ads to process
- `scan_between_ads` (bool, default true): Dwell/scroll between ads (comparison-shopping)
- `scan_dwell_min` (number, default 3 sec)
- `scan_dwell_max` (number, default 8 sec)
- `scan_scroll` (bool, default true): Scroll during scan pause
- `steps` (list): Nested steps (sees {ad.domain}, {ad.clean_url}, etc.)

**Side Effects:**
- Break/continue flags honored
- Scan pause between iterations (not before first, not after last)

---

#### 35. **foreach**
**Scope:** any  
**Category:** flow  
**Description:** Iterate custom list, run nested steps once per item.

**Parameters:**
- `items` (list or string): Explicit list or {var.xxx} reference
- `item_var` (string, default "item"): Placeholder name
- `shuffle` (bool, default true)
- `steps` (list): Nested steps

---

### Flow Control (no nested steps)

#### 36. **break**
**Scope:** any  
**Category:** flow  
**Description:** Stop innermost foreach/loop immediately.

**Parameters:** None

---

#### 37. **continue**
**Scope:** any  
**Category:** flow  
**Description:** Skip to next iteration of innermost loop.

**Parameters:** None

---

### Data Operations

#### 38. **catch_ads**
**Scope:** any  
**Category:** data  
**Description:** Parse ads on current page (no search), save to ctx.ads. Refreshes `captcha_present` flag.

**Parameters:**
- `query` (string, optional): Label for DB stats

**Side Effects:**
- Sets ctx.flags["ads_found"]
- Sets ctx.flags["captcha_present"]

---

#### 39. **save_var**
**Scope:** any  
**Category:** data  
**Description:** Save literal or interpolated value to named variable.

**Parameters:**
- `name` (string, required): Variable name [a-zA-Z_][a-zA-Z0-9_]*
- `value` (string): Literal or {var} template

**Validation:**
- Reserved names rejected: vault, ad, ads, item, query, profile, flag, var, _runtime_raw
- Per-var size cap: 100 KB (truncated with "…(truncated)")

---

#### 40. **extract_text**
**Scope:** any  
**Category:** data  
**Description:** CSS selector → element.text or attribute, save to variable.

**Parameters:**
- `selector` (string, required)
- `save_as` (string, default "extracted"): Variable name
- `all` (bool, default false): Collect all matches as list (vs. first only)

---

### External Integration

#### 41. **http_request**
**Scope:** any  
**Category:** external  
**Description:** Fire plain-HTTP request (NOT through browser proxy). Webhooks, notifications.

**Parameters:**
- `method` (string, default "POST"): GET | POST | PUT | DELETE
- `url` (string, required): Target URL (template-compatible)
- `headers` (dict): HTTP headers
- `body` (dict or string): Request body (auto-JSON if dict)
- `timeout` (number, default 15 sec)
- `save_as` (string): Variable for response

**Security Rules:**
- Only http(s) schemes allowed
- Blocks loopback/RFC1918 (localhost, 127.*, 10.*, 192.168.*, 172.16-31.*)
- Blocks link-local (169.254.*)
- Response capped at 1 MB

**Side Effects:**
- JSON responses auto-parsed
- Non-JSON: text fallback, capped at 10 KB

---

### Extension Automation (Phase 5)

#### 42. **open_extension_popup**
**Scope:** any  
**Category:** extensions  
**Description:** Open extension's default popup HTML in new tab.

**Parameters:**
- `extension_id` (string): 32-char CWS ID (preferred)
- `extension_name` (string): Case-insensitive name match fallback
- `wait_for_selector` (string): CSS selector to wait for after open
- `timeout` (number, default 15 sec)
- `save_handle_as` (string, default "ext_tab"): Variable for tab handle

**Side Effects:**
- Saves `_ext_origin_tab` for cleanup
- Switches to new tab

---

#### 43. **open_extension_page**
**Scope:** any  
**Category:** extensions  
**Description:** Open arbitrary extension page (popup.html, options.html, home.html).

**Parameters:**
- `extension_id` (string)
- `extension_name` (string)
- `page` (string, default "popup.html"): Page name
- `wait_for_selector` (string)
- `timeout` (number, default 15 sec)
- `save_handle_as` (string, default "ext_tab")

---

#### 44. **extension_eval**
**Scope:** any  
**Category:** extensions  
**Description:** Run JavaScript in open extension tab.

**Parameters:**
- `code` (string, required): JS code (function body)
- `store_as` (string): Variable for return value

---

#### 45. **extension_wait_for**
**Scope:** any  
**Category:** extensions  
**Description:** Block until selector appears in extension tab.

**Parameters:**
- `selector` (string, required)
- `timeout` (number, default 15 sec)
- `save_as` (string): Variable for element.textContent if found

---

#### 46. **extension_click**
**Scope:** any  
**Category:** extensions  
**Description:** Click element inside open extension tab (auto-waits for selector).

**Parameters:**
- `selector` (string, required)
- `timeout` (number, default 10 sec)
- `abort_on_error` (bool, default false)

---

#### 47. **extension_fill**
**Scope:** any  
**Category:** extensions  
**Description:** Type into extension input field. Supports {vault.id.field} placeholders.

**Parameters:**
- `selector` (string, required)
- `value` (string, required): Text or {vault.x.y} reference
- `clear_first` (bool, default true)

---

#### 48. **extension_close**
**Scope:** any  
**Category:** extensions  
**Description:** Close extension tab and switch back to original tab.

**Parameters:** None

**Side Effects:**
- Reads `ctx.vars["_ext_origin_tab"]` to find original tab

---

## PART 4: GLOBAL RUNNER FEATURES

### Per-Step Flags (work across all actions)

Applied in `run_pipeline()` and `_exec_single()`:

- **enabled** (bool, default true)
- **probability** (float 0-1, default 1.0)
- **abort_on_error** (bool, default false)
- **skip_on_my_domain** (bool, per-ad only)
- **skip_on_target** (bool, per-ad only)
- **only_on_target** (bool, per-ad only)
- **only_on_my_domain** (bool, per-ad only)

### Variable Substitution

**Pattern:** `{path}` → `{ad.domain}`, `{var.username}`, `{item}`, `{query}`, `{profile}`

**Scope:**
- All string parameters in actions
- Works recursively in dicts/lists

**Resolution** (via `RunContext.resolve_path()`):
- `ad.field` → current ad dict
- `ads.count` → ad count
- `item` → foreach loop var
- `var.name` → saved variable
- `query` → current search query
- `profile` → profile name
- `flag.name` → ctx.flags[name]
- Unknown root → looks in `vars` as convenience

### Context State (RunContext)

- **ad**: Current ad (inside foreach_ad)
- **ads**: List of ads from search/catch
- **item**: Current foreach item
- **vars**: Accumulated variables (live for whole run, shared across scopes)
- **flags**: Boolean flags (ads_found, captcha_present)
- **run_id, profile_name, query**: Metadata

### Watchdog Integration

Long operations (dwell >25s, wait_for_url >25s) wrapped in `_WatchdogShield` to prevent watchdog probe failures.

---

## PART 5: DASHBOARD API ENDPOINTS

### `/api/actions/catalog` (GET)
Returns:
```json
{
  "types": [...action catalog...],
  "common_params": [...shared params...]
}
```

### `/api/actions/pipelines` (GET/POST)
Legacy pipeline retrieval/save (main_script, post_ad_actions, on_target_domain_actions).

### `/api/actions/flow` (GET/POST)
Unified flow (single ordered list). POST clears legacy pipelines.

### `/api/actions/condition-kinds` (GET)
Returns CONDITION_KINDS catalog for condition picker.

---

## KEY OBSERVATIONS FOR DESKTOP PORT

1. **Two dispatch systems coexist:**
   - Legacy: ACTION_HANDLERS + LOOP_ACTION_HANDLERS dicts
   - Unified: Direct `if/elif` chain in `_exec_single()`

2. **Variable interpolation is deep:**
   - Works in all string params
   - Supports dotted paths and list indexing
   - Reserved names and size caps enforced

3. **Per-step flags are critical:**
   - probability, enabled, abort_on_error, domain filters
   - Skipped steps still logged as "skipped"

4. **Persona-driven timing:**
   - click_selector, type actions use profile's typing_wpm / reading_speed
   - Fallbacks to fixed ranges if persona unavailable

5. **Watchdog shielding:**
   - Long dwells/waits >25s bypass probe checks
   - Prevents watchdog kill during slow operations

6. **Extension tab handle management:**
   - Saved in ctx.vars["ext_tab"] and ctx.vars["_ext_origin_tab"]
   - Must clean up tabs on error

---

**Report generated April 2026**

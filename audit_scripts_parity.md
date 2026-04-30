# Scripts Feature: Web vs Desktop Parity Audit

**Date:** April 2026  
**Web source:** `F:\projects\ghost_shell_browser`  
**Desktop port:** `F:\projects\ghost_shell_desktop`  
**Auditors:** 4 parallel agents (web actions, desktop actions, web step UX, desktop step UX)

---

## Executive Summary

The desktop port covers **~30 of 44** web action types and **3 of 7** per-step flags exposed in the UI. Core control flow, navigation, interaction, timing, and ad parsing are present. Missing: extension automation (7 actions), HTTP webhooks, mobile gestures, search-loop helpers, and four per-step domain filters that the web exposes via per-step checkboxes.

This document drives the Phase 17 gap-fill work.

---

## Part A — Per-step flags

| Flag | Web runtime | Web UI | Desktop runtime | Desktop UI | Action |
|------|-------------|--------|-----------------|------------|--------|
| `enabled` | ✓ | ✓ checkbox | ✓ | ✓ checkbox on card | OK |
| `probability` | ✓ (0–1) | ✓ inspector field | ✓ (0–1) | ✗ not exposed | **ADD UI** |
| `abort_on_error` | ✓ | ✓ inspector field | ✓ (`AbortOnError`) | ✗ not exposed | **ADD UI** |
| `skip_on_my_domain` | ✓ per-ad | ✓ checkbox | ✗ | ✗ | **ADD model + runtime + UI** |
| `skip_on_target` | ✓ per-ad | ✓ checkbox | ✗ | ✗ | **ADD model + runtime + UI** |
| `only_on_target` | ✓ per-ad | ✓ checkbox | ✗ | ✗ | **ADD model + runtime + UI** |
| `only_on_my_domain` | ✓ per-ad | ✓ checkbox | ✗ | ✗ | **ADD model + runtime + UI** |
| `label` | — | — | ✓ exists | ✗ unused | OK (low priority) |

The four domain-filter flags only matter inside `foreach_ad` — they early-skip a step when the current ad's domain doesn't match the policy. We need them so users can build "click only competitor ads" or "skip self-clicks" pipelines without writing a manual `if`.

---

## Part B — Action catalog gaps

### Present in both (29 actions, parity OK)

`navigate`, `back`, `forward`, `reload`, `new_tab`, `close_tab`, `dwell`, `random_delay`, `wait_for_selector`, `wait_for_url`, `click_selector`, `double_click`, `right_click`, `hover`, `type`, `press_key`, `scroll`, `scroll_to_bottom`, `fill_form`, `move_random`, `save_var`, `extract_text`, `execute_js`, `parse_ads` (`catch_ads` alias), `click_ad`, `solve_captcha`, `screenshot`, `log`, `if`, `foreach`, `foreach_ad`, `break`, `continue`

### In palette but no runner (1 action)

| Action | Status |
|--------|--------|
| `while_loop` | ❌ Listed in palette, throws `NotSupportedException` at runtime |

### Missing entirely (15 actions)

| Action | Category | Priority | Notes |
|--------|----------|----------|-------|
| `read` | interaction | **HIGH** | Scroll-pause-repeat to simulate reading. Common warmup primitive. |
| `switch_tab` | navigation | **HIGH** | Switch to tab by index. |
| `pause` | timing | MEDIUM | Effectively dwell; alias for compat. |
| `refresh` | navigation | MEDIUM | Reload N times with backoff (SERP retry). |
| `rotate_ip` | network | MEDIUM | Force proxy rotation (no-op on static). |
| `http_request` | external | **HIGH** | Webhook / API call with SSRF guards. |
| `touch_click` | mobile | LOW | CDP `Input.dispatchTouchEvent`. |
| `swipe` | mobile | LOW | Multi-step touchMove. |
| `search_query` | loop | LOW | Google SERP scraping; specialized to web warmup. |
| `search_all_queries` | loop | LOW | Iterate DB queries. |
| `open_extension_popup` | extensions | MEDIUM | Open extension popup HTML. |
| `open_extension_page` | extensions | MEDIUM | Open extension page (options.html etc). |
| `extension_eval` | extensions | MEDIUM | Run JS in extension tab. |
| `extension_wait_for` | extensions | MEDIUM | Wait for selector in extension tab. |
| `extension_click` | extensions | MEDIUM | Click inside extension tab. |
| `extension_fill` | extensions | MEDIUM | Type into extension input (vault placeholders). |
| `extension_close` | extensions | MEDIUM | Close ext tab, switch back. |

---

## Part C — Conditions catalog gaps

The desktop has 15 condition kinds; the web has 14. Names differ slightly. Real gaps:

| Condition | Web | Desktop | Action |
|-----------|-----|---------|--------|
| `always` | ✓ | ✓ (`true`) | OK |
| `ads_found` | ✓ | ✓ (`has_ads`) | OK |
| `no_ads` | ✓ | via `not has_ads` | OK |
| `ads_count_gte` | ✓ | ✓ | OK |
| `ad_is_competitor` | ✓ | ✗ | **ADD** |
| `ad_is_external` | ✓ | ✗ | **ADD** |
| `ad_is_target` | ✓ | ✗ | **ADD** |
| `ad_is_mine` | ✓ | ✗ (`own_domain` is similar) | OK |
| `captcha_present` | ✓ | ✓ (`captcha_visible`) | OK |
| `url_contains` | ✓ | ✓ | OK |
| `url_matches` | — | ✓ | desktop bonus |
| `element_exists` | ✓ | ✓ (`selector_present`) | OK |
| `selector_visible` | — | ✓ | desktop bonus |
| `var_equals` | ✓ | ✓ | OK |
| `var_contains` | ✓ | ✗ (`var_matches` regex covers it) | OK |
| `var_matches` | ✓ | ✓ | OK |
| `var_empty` | ✓ | via `not var_exists` | OK |
| `var_exists` | — | ✓ | desktop bonus |
| `random` | — | ✓ (probability gate) | desktop bonus |
| `title_contains` | — | ✓ | desktop bonus |

**Real gaps:** `ad_is_competitor`, `ad_is_external`, `ad_is_target` (and rename `own_domain` to canonical `ad_is_mine` alias).

---

## Part D — Editor UX gaps

| Feature | Web | Desktop | Priority |
|---------|-----|---------|----------|
| Drag-drop reorder | ✓ | ✗ (Up/Down only) | LOW |
| Up/Down arrows | ✗ | ✓ | OK |
| Duplicate step | ✓ | ✗ | MEDIUM |
| Delete step | ✓ | ✓ | OK |
| Inline param accordion | ✓ | ✗ (modal only) | LOW |
| JSON view | ✗ | ✓ | desktop bonus |
| Schema-driven typed form | ✓ | ✓ | OK |
| Variable picker (🔑) | ✓ | ✗ | LOW |
| Lint warnings | ✓ | partial | LOW |
| Visual category color | ✓ | ✓ | OK |
| Templates gallery | ✓ | ❌ "coming soon" | MEDIUM |
| Import / Export | ✓ | ✓ | OK |
| Apply to profiles | ✓ | ✓ | OK |
| Run-from-editor | ✓ | ✗ (Profiles page only) | LOW |
| Per-step probability slider | ✓ | ✗ | **HIGH (Part A)** |
| Per-step abort_on_error | ✓ | ✗ | **HIGH (Part A)** |
| Per-step domain filters | ✓ | ✗ | **HIGH (Part A)** |

---

## Phase 17 Implementation Plan

### Tier 1 — must ship (per-step flags + key missing actions)

1. Extend `ScriptStep` model: add `SkipOnMyDomain`, `SkipOnTarget`, `OnlyOnTarget`, `OnlyOnMyDomain`.
2. Extend `ScriptRunner` to honour these four flags inside `foreach_ad`.
3. Add a new "advanced" panel on the step card / typed-form dialog exposing:
   probability slider, abort_on_error, skip_on_my_domain, skip_on_target, only_on_target, only_on_my_domain.
4. Implement `while_loop` in runner (already in palette).
5. Add actions: `read`, `switch_tab`, `pause`, `refresh`, `rotate_ip`, `http_request`.
6. Add condition kinds: `ad_is_competitor`, `ad_is_external`, `ad_is_target`, `ad_is_mine` (alias of `own_domain`).

### Tier 2 — follow-up (extensions + mobile)

7. Extension actions (7): open_extension_popup, open_extension_page, extension_eval, extension_wait_for, extension_click, extension_fill, extension_close.
8. Mobile actions (2): touch_click, swipe.

### Tier 3 — UX polish

9. Duplicate step button.
10. Templates gallery.
11. Variable picker (🔑) for inserting `{var.x}` / `{ad.domain}` etc.
12. Drag-drop reorder.

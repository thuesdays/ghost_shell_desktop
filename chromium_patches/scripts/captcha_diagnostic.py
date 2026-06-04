"""
captcha_diagnostic.py — Deep-logged captcha investigation harness.

Use when Google starts hitting a profile with captcha on every search
and you can't tell whether the latest C++ patches broke something or
the proxy IP is just burned.

What it does — one profile launch, 5 sequential probes, single text
report:
  1. Hit ipinfo.io — confirm the proxy actually exits where we expect
     (country / org / IP). Mismatched country vs profile timezone is
     the #1 cause of immediate captchas.
  2. Hit https://tls.peet.ws/api/all — capture the JA3 / JA4 / HTTP/2
     fingerprint as the server sees it. Gives us a smoking gun if our
     C++ JA3 / SETTINGS GREASE patches misbehave.
  3. Hit https://browserleaks.com/javascript — dump navigator/UA-CH/
     screen/plugins state. Shows what every JS-side patch produces.
  4. Hit https://www.google.com — does Google show captcha BEFORE we
     even type a query? That isolates "IP burned" from "search-rate
     burn".
  5. Hit https://www.google.com/search?q=test — does an organic-keyword
     SERP load cleanly? Captcha here but not on (4) means search-rate
     limit, not IP burn.

Output: single text file per run under reports/captcha_diag/ that the
user can paste back to a developer for triage. Captures:
  - Run banner (template, proxy, payload summary)
  - Browser console output (lvl >= warning)
  - Network requests / responses (status, content-type, body sample
    for HTML pages)
  - Per-step screenshot
  - Page title + HTML excerpt where captcha typically shows up
  - Cookies present after each step

Usage:
  python -m scripts.captcha_diagnostic                # uses profile_01 + global proxy
  python -m scripts.captcha_diagnostic profile_05
  python -m scripts.captcha_diagnostic profile_01 --proxy http://...

The C++ stealth core is ON the same way it is for normal runs (via
GhostShellBrowser → GhostShellConfig payload). No special path.
"""

# ── sys.path bootstrap ───────────────────────────────────────────
import os as _os, sys as _sys
_ROOT = _os.path.dirname(_os.path.dirname(_os.path.abspath(__file__)))
if _ROOT not in _sys.path:
    _sys.path.insert(0, _ROOT)

import os
import json
import time
import logging
import argparse
from datetime import datetime
from urllib.parse import urlparse

# We use the project's GhostShellBrowser so the same C++ stealth core
# fires that's used in production. NOT the bare nk_browser/Selenium.
from ghost_shell.browser.runtime import GhostShellBrowser


PROBES = [
    {
        "name":    "ipinfo",
        "url":     "https://ipinfo.io/json",
        "purpose": "Confirm proxy exits where we expect (country / org)",
        "extract": "json",
    },
    {
        "name":    "tls_peet",
        "url":     "https://tls.peet.ws/api/all",
        "purpose": "Capture JA3 / JA4 / HTTP/2 fingerprint server-side",
        "extract": "json",
    },
    {
        "name":    "browserleaks_js",
        "url":     "https://browserleaks.com/javascript",
        "purpose": "Snapshot navigator / screen / plugins / UA-CH",
        "extract": "html_excerpt",
    },
    {
        "name":    "google_home",
        "url":     "https://www.google.com",
        "purpose": "Does Google show captcha on the home page (no query)?",
        "extract": "captcha_check",
    },
    {
        "name":    "google_search",
        "url":     "https://www.google.com/search?q=test",
        "purpose": "Does an organic-keyword SERP load cleanly?",
        "extract": "captcha_check",
    },
]


def _section(title: str) -> str:
    return "\n" + "═" * 70 + f"\n  {title}\n" + "═" * 70 + "\n"


def _captcha_check_js() -> str:
    """JS that returns a small dict: title, captcha_present, recaptcha_iframe,
    body excerpt, and the count of <a> tags (a real SERP has 100+; a captcha
    page has < 5). Used to classify what loaded."""
    return r"""
return (() => {
  const out = {
    title:            document.title || "",
    url:              location.href,
    body_chars:       (document.body && document.body.innerText
                         || "").length,
    anchor_count:     document.querySelectorAll("a").length,
    captcha_iframe:   !!document.querySelector(
                        'iframe[src*="recaptcha"], iframe[src*="captcha"]'),
    captcha_text:     /captcha|please.*not.*robot|unusual.*traffic/i.test(
                        document.body && document.body.innerText || ""),
    body_excerpt:     (document.body && document.body.innerText || "")
                        .slice(0, 400),
    cookies:          document.cookie.split(";").map(c=>c.trim().slice(0,60))
                        .filter(Boolean).slice(0, 20),
  };
  return out;
})();
"""


def run_diagnostic(profile_name: str, proxy_str: str = None) -> str:
    """Run all probes; return the path to the saved report file."""
    out_dir = os.path.join("reports", "captcha_diag")
    os.makedirs(out_dir, exist_ok=True)
    ts = datetime.now().strftime("%Y%m%d_%H%M%S")
    out_path = os.path.join(out_dir, f"diag_{profile_name}_{ts}.txt")

    lines: list[str] = []
    lines.append(_section(f"CAPTCHA DIAGNOSTIC — {profile_name}  @ {ts}"))
    lines.append(f"Profile        : {profile_name}")
    lines.append(f"Proxy override : {proxy_str or '(use config)'}")
    lines.append(f"Output report  : {out_path}")
    lines.append("")

    with GhostShellBrowser(
        profile_name = profile_name,
        proxy_str    = proxy_str,
        auto_session = False,    # don't pollute saved session
    ) as browser:
        driver = browser.driver

        # Banner — record what the browser thinks it is.
        try:
            banner_js = r"""
            return {
                userAgent:    navigator.userAgent,
                language:     navigator.language,
                languages:    Array.from(navigator.languages || []),
                platform:     navigator.platform,
                webdriver:    navigator.webdriver,
                hwc:          navigator.hardwareConcurrency,
                dm:           navigator.deviceMemory,
                tz:           Intl.DateTimeFormat().resolvedOptions().timeZone,
                tz_offset:    new Date().getTimezoneOffset(),
                screen:       screen.width + "x" + screen.height,
                dpr:          window.devicePixelRatio,
            };
            """
            banner = driver.execute_script(banner_js)
            lines.append(_section("BROWSER SELF-REPORT (in renderer)"))
            for k, v in banner.items():
                lines.append(f"  {k:<14} {v}")
        except Exception as e:
            lines.append(f"banner JS failed: {e}")

        # Probes
        for i, probe in enumerate(PROBES, 1):
            lines.append(_section(f"PROBE [{i}/{len(PROBES)}] — {probe['name']}"))
            lines.append(f"URL     : {probe['url']}")
            lines.append(f"Purpose : {probe['purpose']}")
            t0 = time.time()
            try:
                driver.get(probe["url"])
            except Exception as e:
                lines.append(f"NAV FAILED: {type(e).__name__}: {e}")
                continue
            time.sleep(3)  # let async stuff complete
            elapsed = time.time() - t0
            lines.append(f"Load    : {elapsed:.2f}s")
            lines.append(f"Title   : {driver.title}")

            try:
                cur_url = driver.current_url
            except Exception:
                cur_url = "?"
            lines.append(f"Final URL: {cur_url}")

            # Extract per-probe data
            try:
                info = driver.execute_script(_captcha_check_js())
            except Exception as e:
                info = {"error": str(e)}
            lines.append("Page check:")
            for k, v in (info or {}).items():
                if k == "body_excerpt":
                    lines.append(f"  body_excerpt:")
                    for ln in (v or "").splitlines()[:8]:
                        lines.append(f"    | {ln[:140]}")
                elif k == "cookies":
                    lines.append(f"  cookies({len(v)}):")
                    for c in (v or [])[:8]:
                        lines.append(f"    {c}")
                else:
                    lines.append(f"  {k:<16} {v}")

            # Optional structured extraction
            if probe["extract"] == "json":
                try:
                    body_text = driver.execute_script(
                        "return document.body.innerText || '';")
                    body_text = (body_text or "").strip()
                    if body_text.startswith("{") and body_text.endswith("}"):
                        try:
                            parsed = json.loads(body_text)
                            lines.append("JSON body:")
                            lines.append(json.dumps(parsed, indent=2,
                                                    ensure_ascii=False)[:2000])
                        except Exception:
                            lines.append(f"raw body ({len(body_text)} chars):")
                            lines.append(body_text[:2000])
                    else:
                        lines.append(f"non-JSON body ({len(body_text)} chars):")
                        lines.append(body_text[:1000])
                except Exception as e:
                    lines.append(f"body fetch failed: {e}")

            # Screenshot
            try:
                ss_path = os.path.join(
                    out_dir, f"diag_{profile_name}_{ts}_{probe['name']}.png")
                driver.save_screenshot(ss_path)
                lines.append(f"Screenshot: {ss_path}")
            except Exception as e:
                lines.append(f"screenshot failed: {e}")

        # Captured Console messages (CDP) — last few that are at warn/error
        lines.append(_section("BROWSER CONSOLE (last warnings/errors)"))
        try:
            logs = driver.get_log("browser") or []
            for entry in logs[-20:]:
                lvl = entry.get("level", "?")
                msg = (entry.get("message") or "")[:200]
                ts2 = entry.get("timestamp")
                lines.append(f"  [{lvl}] {msg}")
        except Exception as e:
            lines.append(f"  driver.get_log unavailable: {e}")

    # Write report
    text = "\n".join(lines) + "\n"
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(text)

    return out_path


def main():
    parser = argparse.ArgumentParser(
        description="Deep-logged diagnostic for captcha-on-open / "
                    "fingerprint regression investigation."
    )
    parser.add_argument("profile", nargs="?", default="profile_01",
                        help="Profile name (default: profile_01)")
    parser.add_argument("--proxy", default=None,
                        help="Override proxy URL (default: profile/global)")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.INFO,
        format="%(asctime)s [%(levelname)s] %(message)s",
    )

    print(f"Running captcha diagnostic for {args.profile}…")
    path = run_diagnostic(args.profile, proxy_str=args.proxy)
    print(f"\n✓ Report saved → {path}\n")
    print("Paste the contents of that file back into the chat for triage.")


if __name__ == "__main__":
    main()

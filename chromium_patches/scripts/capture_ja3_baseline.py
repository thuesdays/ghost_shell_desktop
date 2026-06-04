"""
capture_ja3_baseline.py — one-shot helper to populate
EXPECTED_JA3_BY_MAJOR.

Sprint 3.2 left the EXPECTED_JA3_BY_MAJOR table empty on purpose —
fabricated placeholder hashes would penalize every legit run. This
script captures the real hash.

Three modes:

1) AUTO  — launches a headless STOCK Chrome (NOT our patched
   Chromium), navigates to check.ja3.zone/json, parses the JSON,
   prints the code snippet you append to ja3_check.py.

2) MANUAL — you paste a JSON body you copied from check.ja3.zone
   in your own Chrome. Useful when stock Chrome isn't on PATH.

3) FROM-PROFILE — reuses an already-running Ghost Shell profile's
   driver (uncommon but handy when you want to validate the patched
   build against itself).

Usage:
    python scripts/capture_ja3_baseline.py                     # AUTO
    python scripts/capture_ja3_baseline.py --manual            # paste mode
    python scripts/capture_ja3_baseline.py --major 149         # override
    python scripts/capture_ja3_baseline.py --label win10-x64   # nickname

The output is a Python snippet you copy/paste into ``EXPECTED_JA3_BY_MAJOR``
in ``ghost_shell/fingerprint/ja3_check.py``.
"""

from __future__ import annotations

__author__ = "Mykola Kovhanko"
__email__ = "thuesdays@gmail.com"

import argparse
import json
import sys
import os


REPORT_URL = "https://check.ja3.zone/json"


def capture_auto(timeout: float = 25.0) -> dict:
    """Mode 1 — launch a stock Chrome via selenium and read JA3.

    Tries to use a system-installed Chrome (not our patched binary)
    so the captured hash represents the upstream baseline. Falls
    back to selenium-manager auto-download if no Chrome is found."""
    try:
        from selenium import webdriver
        from selenium.webdriver.chrome.options import Options
    except ImportError:
        print("[!] selenium not available — install dev deps or use --manual",
              file=sys.stderr)
        return {}

    opts = Options()
    opts.add_argument("--headless=new")
    opts.add_argument("--no-sandbox")
    opts.add_argument("--disable-gpu")
    opts.add_argument("--disable-dev-shm-usage")

    print(f"[i] Launching stock Chrome (headless) → {REPORT_URL}",
          file=sys.stderr)
    driver = None
    try:
        driver = webdriver.Chrome(options=opts)
        driver.set_page_load_timeout(timeout)
        driver.get(REPORT_URL)
        # The endpoint returns JSON wrapped in a <pre> tag in headless
        # Chrome — extract from page source.
        body = driver.find_element("tag name", "body").text or ""
        try:
            return json.loads(body)
        except json.JSONDecodeError:
            print(f"[!] Couldn't parse JSON from page body. Raw "
                  f"first-200-chars: {body[:200]!r}", file=sys.stderr)
            return {}
    except Exception as e:
        print(f"[!] AUTO mode failed: {type(e).__name__}: {e}",
              file=sys.stderr)
        return {}
    finally:
        if driver is not None:
            try: driver.quit()
            except Exception: pass


def capture_manual() -> dict:
    """Mode 2 — paste the JSON body manually."""
    print(f"[i] MANUAL mode. Visit {REPORT_URL} in your browser, copy",
          file=sys.stderr)
    print("    the entire JSON response, paste here, then press",
          file=sys.stderr)
    print("    Ctrl+D (Linux/Mac) or Ctrl+Z+Enter (Windows):", file=sys.stderr)
    text = sys.stdin.read().strip()
    if not text:
        print("[!] Empty input.", file=sys.stderr)
        return {}
    try:
        return json.loads(text)
    except json.JSONDecodeError as e:
        print(f"[!] JSON parse failed: {e}", file=sys.stderr)
        return {}


def detect_major_from_response(data: dict) -> int | None:
    """Try to read the Chrome major from data['user_agent'] —
    check.ja3.zone reflects the UA back."""
    ua = data.get("user_agent") or ""
    import re
    m = re.search(r"Chrome/(\d+)\.", ua)
    return int(m.group(1)) if m else None


def emit_snippet(data: dict, major: int, label: str | None) -> None:
    """Print a copy/paste-able snippet that the user appends to
    EXPECTED_JA3_BY_MAJOR in ja3_check.py."""
    ja3 = data.get("ja3")
    if not ja3:
        print("[!] No 'ja3' field in response. Got keys: "
              f"{list(data.keys())}", file=sys.stderr)
        return
    if not isinstance(ja3, str) or len(ja3) != 32:
        print(f"[!] 'ja3' field has unexpected shape: {ja3!r}",
              file=sys.stderr)
        return
    try:
        int(ja3, 16)
    except ValueError:
        print(f"[!] 'ja3' is not hex: {ja3!r}", file=sys.stderr)
        return

    label_str = f"  # {label}" if label else ""
    print()
    print("─" * 70)
    print("  Append this entry to ja3_check.py → EXPECTED_JA3_BY_MAJOR")
    print("─" * 70)
    print()
    print(f"    {major}: [")
    print(f"        \"{ja3.lower()}\",{label_str}")
    print(f"        # captured: {data.get('user_agent','?')[:80]}")
    print(f"        # from IP : {data.get('ip','?')}")
    print(f"        # tls_ver : {data.get('tls_version','?')}")
    print(f"    ],")
    print()
    print("─" * 70)
    print(f"  Or, if {major} is already there, add the new hash inside its list.")
    print("─" * 70)


def main():
    ap = argparse.ArgumentParser(description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--manual", action="store_true",
                    help="Paste the JSON body from check.ja3.zone manually.")
    ap.add_argument("--major", type=int, default=None,
                    help="Override Chrome major version (auto-detected from "
                         "UA otherwise).")
    ap.add_argument("--label", type=str, default=None,
                    help="Nickname to add as a comment (e.g. "
                         "'win10-x64', 'mac-m2', 'linux-mesa').")
    ap.add_argument("--timeout", type=float, default=25.0,
                    help="Per-action timeout for AUTO mode (default 25s).")
    args = ap.parse_args()

    data = capture_manual() if args.manual else capture_auto(args.timeout)
    if not data:
        sys.exit(1)

    major = args.major or detect_major_from_response(data)
    if not major:
        print("[!] Could not detect Chrome major version. Pass --major.",
              file=sys.stderr)
        sys.exit(2)

    emit_snippet(data, major, args.label)


if __name__ == "__main__":
    main()

# Ghost Shell — Chromium stealth patches

Vendored copy of the native (C++) anti-detect patches applied to the
Chromium source tree, kept here in the desktop repo for consistency,
review, and maintenance. **Source of truth:** the working tree at
`F:\projects\chromium\src` (a git checkout of Chromium **149.0.7805.0**).
The older partial snapshot at `F:\projects\ghost_shell_browser\с++\`
(43 files, Cyrillic `с`) is **superseded by this directory** — it was
stale/incomplete (missing navigator.cc, screen_orientation, v8_initializer,
permission_controller, BUILD.gn/DEPS, chrome_main_delegate, etc.).

## Contents

- `ghost-shell-149.0.7805.0.patch` — `git diff` of every **modified**
  source file (text/code only; binary rebranding assets excluded).
  ~1.78k inserted lines across 47 code files.
- `new_files/` — the **new** files Chromium doesn't ship, copied with
  their target paths:
  - `third_party/blink/renderer/platform/ghost_shell_config.{h,cc}` —
    the central per-profile config core: parses the `--ghost-shell-payload`
    JSON and exposes typed getters (hardware, screen, GPU, audio, timezone,
    battery, connection, fonts, **noise seeds**, media devices, codecs,
    UA-metadata, plugins, permissions, **TLS/JA3**). Also exposes a
    `extern "C"` bridge (`GhostShell_IsActive`, `GhostShell_GetRandomSeed`,
    `GhostShell_GetTLSCipherSuites/SupportedGroups`) for BoringSSL/WebRTC.
  - `components/embedder_support/ghost_shell_ua_override.{h,cc}` — reads
    the payload flag and feeds `GetUserAgentMetadata()` (Sec-CH-UA-*).
- Binary/branding changes (NOT in the patch — list only): product logos
  (`*.png`) and icons (`*.ico`) under `chrome/app/theme/**` and
  `chrome/installer/**`. 19 assets. Re-apply by copying the branded
  assets, not via the text patch.

## Patched vectors (what each area spoofs)

| Area | Files | Vector |
|---|---|---|
| Payload core | `ghost_shell_config.*`, `ghost_shell_ua_override.*`, `user_agent_utils.cc`, `chrome_main_delegate.cc`, `chrome_content_browser_client.cc`, `v8_initializer.cc`, `BUILD.gn`, `DEPS` | flag parse, UA/UA-CH, config plumbing |
| navigator.* | `navigator.cc`, `navigator_base.cc`, `navigator_id.cc`, `navigator_language.cc`, `navigator_concurrent_hardware.cc`, `navigator_device_memory.cc`, `local_dom_window.cc` | UA, platform, languages, hardwareConcurrency, deviceMemory |
| Canvas/2D | `base_rendering_context_2d.cc`, `text_metrics.cc`, `image_data_buffer.cc` | canvas hash noise, text metrics |
| WebGL/WebGPU | `webgl_rendering_context_base.cc`, `webgpu/gpu_adapter.cc`, `gpu_adapter_info.cc` | UNMASKED vendor/renderer, params, extensions, WebGPU adapter |
| Audio | `webaudio/base_audio_context.*`, `audio_buffer.*`, `realtime_analyser.cc` | sampleRate, base latency, frequency-data noise |
| Screen/geometry | `screen.cc`, `screen_orientation.cc`, `dom_rect_read_only.h`, `element.cc`, `performance.cc` | screen/avail, orientation, getClientRects noise, perf timing |
| Fonts | `font_cache.cc`, `font_access.cc` | installed-font enumeration |
| Devices/misc | `media_devices.cc`, `media_capabilities.cc`, `permissions.cc`, `permission_controller_impl.cc`, `speech_synthesis.cc`, `battery_manager.cc`, `network_information.cc`, `event.cc` | enumerateDevices, codecs, permissions, voices, battery, NetInfo, **isTrusted** |
| TLS/JA3 | `net/socket/ssl_client_socket_impl.cc`, `third_party/boringssl/src/ssl/extensions.cc` | cipher suites, supported groups, extension order |
| HTTP/2 + WebRTC | `net/spdy/spdy_session.cc`, `webrtc/pc/peer_connection.cc`, `services/network/p2p/socket_manager.cc` | H2 SETTINGS/akamai fp, WebRTC IP handling |

## Applying to a fresh Chromium 149.0.7805.0 checkout

```bash
cd /path/to/chromium/src
git apply /path/to/chromium_patches/ghost-shell-149.0.7805.0.patch
# copy the new files into place
cp -r /path/to/chromium_patches/new_files/* .
# re-apply branded logos/icons (binary) from the brand asset set
# then: gn gen out/Release && autoninja -C out/Release chrome
```

> Keep this directory current after ANY change in the checkout — run
> `pwsh -File chromium_patches\sync_patches.ps1` (regenerates the patch +
> re-copies new core files). This is the canonical "keep patches up to
> date in the repo" step.

## Building (out/GhostShell)

Build dir: `F:\projects\chromium\src\out\GhostShell` (Release: `is_debug=false`,
`symbol_level=0`, `is_component_build=false`, `enable_direct_composition=false`).
Build tool is **Siso** (use `autoninja`, not raw `ninja`). depot_tools at
`F:\projects\depot`.

```bat
set PATH=F:\projects\depot;%PATH%
cd /d F:\projects\chromium\src
autoninja -C out\GhostShell chrome chromedriver crashpad_handler
```

The full build+deploy is wrapped by `scripts\build-ghost-shell.bat` (also
`.sh`), which builds those three targets, reads `chrome\VERSION`, and copies
the runtime set (chrome.exe/.dll, chrome_elf.dll, d3dcompiler_47.dll,
libEGL/libGLESv2, vk_swiftshader, *.pak, v8_context_snapshot.bin, icudtl.dat,
the SxS `<version>.manifest` — **critical**, crashpad_handler.exe,
chromedriver.exe, `locales\`) into a flat `chrome_win64\` deploy dir.

## scripts/

Only the scripts actually needed for **our (Windows desktop) build** are kept
in-repo (the web-version / fetch-prebuilt / branding / QA scripts from
`ghost_shell_browser\scripts` were intentionally dropped):

| Script | Purpose |
|---|---|
| `build-ghost-shell.bat` | `autoninja` build of chrome + chromedriver + crashpad_handler, then flat-deploy the runtime set to `chrome_win64\`. Flags: `/clean` (wipe `out\GhostShell` first), `/skip-build` (deploy an existing build only). |
| `package_chromium.ps1` | pack `chrome_win64\` → `dist\chrome_win64-vX.Y.Z.W.zip` **+ `.sha256`** (the sibling checksum the desktop self-update verifies — see audit DATA-01). |

> Note: `build-ghost-shell.bat`'s `DEPLOY_DIR` still points at the legacy
> `F:\projects\goodmedika\chrome_win64` path — update it to the desktop's
> `chrome_win64\` location before use. The CreepJS / JA3 / captcha QA tools
> still live in `ghost_shell_browser\scripts` if build-verification tooling
> is wanted later.

## Audit

See `AUDIT_CHROMIUM_PATCHES_2026-06-04.md` (repo root) for the deep audit:
coverage gaps, currency vs Chromium 149, detection leaks, and the
strengthening / new-patch roadmap.

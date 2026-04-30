# Chromium bundle layout

Ghost Shell ships **patched Chromium 149.0.7805.0** alongside the
desktop app. The installer drops both into the same install folder
so the launcher resolves Chromium without any user configuration.

## Production install layout

```
C:\Program Files\GhostShell\           ← per-machine install
├── GhostShell.exe                     ← main app (WPF)
├── GhostShell.Core.dll
├── GhostShell.Data.dll
├── GhostShell.Runtime.dll
├── *.dll, locales/, …                 ← .NET runtime bits
└── chromium\                          ← bundled patched browser
    ├── chrome.exe                     ← patched Chromium 149.0.7805.0
    ├── chromedriver.exe               ← matching driver
    ├── *.dll                          ← Chromium dependencies
    ├── locales/
    ├── resources/
    └── …
```

Per-user install lives at `%LocalAppData%\Programs\GhostShell\` with
the same layout — no special handling needed.

## Path resolution chain

[`ChromiumLocator`](../src/GhostShell.Runtime/Browser/ChromiumLocator.cs)
walks these candidates in order; first match wins:

| Priority | Path                                              | When used                       |
|---------:|---------------------------------------------------|---------------------------------|
| 1        | `%GHOSTSHELL_CHROMIUM_DIR%`                       | Tests / CI / manual override    |
| 2        | `<install>\chromium\`                             | **Production (installer drop)** |
| 3        | `<install>\chrome_win64\`                         | Alt name (legacy build scripts) |
| 4        | `F:\projects\ghost_shell_browser\chrome_win64\`   | Dev — running from source       |
| 5        | `F:\projects\chromium\src\out\GhostShell\`        | Chromium devs working on patches |
| 6        | `C:\src\chromium\src\out\GhostShell\`             | Same, alternate root            |
| 7        | `C:\src\chromium\src\out\Default\`                | Default Chromium build output   |

Each candidate must contain BOTH `chrome.exe` and `chromedriver.exe`,
otherwise the locator keeps walking.

## Per-profile data

Chromium's `--user-data-dir` is set per profile:

```
%LocalAppData%\GhostShell\profiles\<profile-name>\
├── Default\               ← actual profile dir Chromium creates
│   ├── Cookies, History, Login Data, …
│   ├── Local Storage\
│   ├── Extensions\
│   └── …
├── ghost_shell.lock      ← run lock (Phase 4)
└── …
```

Multiple profiles can run in parallel — each has its own user-data-dir
so cookies / extensions / sessions don't bleed across.

## Installer plan (Phase 7)

WiX 4 MSI bundles:
- All `bin\Release\net8.0-windows\publish\*` from GhostShell.App
- Patched Chromium build copied from `F:\projects\ghost_shell_browser\chrome_win64\`
- Code-signed with EV cert before release

Installer adds:
- Start Menu group `Ghost Shell\` with shortcut to GhostShell.exe
- File associations: `.ghpack` (cookie packs), `.ghscript` (recorded scripts)
- AUMID for native Windows toast notifications

Sizes (rough):
- Desktop app: ~15-25 MB
- Patched Chromium: ~250 MB
- Total MSI: ~80-100 MB compressed

## Dev workflow

1. Build patched Chromium normally in `F:\projects\chromium\src\out\GhostShell\`.
2. Run `package_chromium.bat` from legacy ghost_shell_browser repo to copy into `chrome_win64/`.
3. `dotnet run --project src\GhostShell.App` — locator finds it via candidate #4.

To override:
```powershell
$env:GHOSTSHELL_CHROMIUM_DIR = "D:\custom\chrome\folder"
dotnet run --project src\GhostShell.App
```

## Troubleshooting

**"Patched Chromium build not found"** — Settings page shows the
candidate list it tried. Either:
- Set `GHOSTSHELL_CHROMIUM_DIR` to a folder containing
  `chrome.exe` + `chromedriver.exe`, OR
- Drop the build into `<install>\chromium\` next to `GhostShell.exe`.

**Profile launches, browser opens, then immediately closes** — check
`%LocalAppData%\GhostShell\logs\app-YYYY-MM-DD.log`. Most often the
chromedriver version doesn't match the chrome.exe version. Repackage
the bundle so both come from the same Chromium build.

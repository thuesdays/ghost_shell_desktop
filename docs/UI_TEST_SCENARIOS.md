<!--
  SPDX-License-Identifier: MIT
  Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>
-->
# GhostShell — UI test scenarios & automation reference

Repeatable manual + automated checks for the desktop UI. Every interactive
surface that matters for automation now carries a **stable
`AutomationProperties.AutomationId`** (see the catalog in §6) so a harness
(FlaUI / WinAppDriver / Windows-MCP / Appium-Windows) can target elements by id
instead of fragile coordinates.

> **WPF screenshot caveat:** remote pixel-capture of the GhostShell window can
> return a *stale frame* (hardware-composited WPF + some capture backends). For
> automated assertions, read the **UI Automation element tree** (names / values /
> AutomationIds), which is always live — don't assert on screenshot pixels.

---

## 0. Prerequisites / setup

1. Run the freshly-built `GhostShell.exe`
   (`src/GhostShell.App/bin/Debug/net8.0-windows/`). A DI fatal at start = stale
   build → rebuild / relaunch.
2. **Demo dataset** (profiles + vault + farm group). Either create it by hand
   (scenarios below) or run the seeding harness in **Appendix A** once, which
   creates:
   - vault master = `GhostShell-Master-2026!`
   - profiles `farm_alpha_01/02/03` in group **Crypto Farm Alpha** (cap 3)
   - 3 × `crypto_wallet` vault items (public funded addresses) + 1 `account`
   - writes credentials to `Desktop\GhostShell_credentials.txt`
3. Internet is required for the balance / chain scenarios (read-only public RPC).

---

## 1. Overview dashboard — rendering
**Goal:** hero cards render big/clear with live counts.
**Steps (auto):** click `nav.overview` → assert `page.overview` present.
**Expect:** tiles `overview.tile.runs/successRate/profiles/vault/traffic` visible;
numbers are large/bold; clicking a tile navigates (runs→Runs, profiles→Profiles,
vault→Vault, traffic→Traffic).
**Pass:** counts reflect DB (Profiles tile = profile count).

## 2. Fingerprint — device template cards
**Steps:** click `nav.fingerprint` → Device templates section.
**Expect:** 2-column cards, glyph 💻/🖥/📱 + device name + mono id + spec pills
(CPU / RAM / GPU / 🖥 resolution) + weight badge; hover highlights; click switches
the profile's template and re-scores.

## 3. Vault — unlock + items render
**Steps (auto):**
1. click `nav.vault`.
2. If locked: click `vault.unlock` (or `vault.unlock.cta`) → in the dialog type
   into `vault.passphrase` = master → click `vault.unlock.submit`.
3. Assert: toolbar shows `vault.add` / `vault.lock` / `vault.changePassword`;
   the items grid lists the seeded rows (3 wallets + Demo Gmail).
**Negative:** wrong passphrase → dialog stays open, shows "Wrong passphrase".
**Pass:** unlocked state renders item rows; no "Vault locked" element remains in
the tree.

## 4. Profiles — list renders
**Steps:** click `nav.profiles`.
**Expect:** a card per profile — `profiles.row.farm_alpha_01/02/03` present (+ any
pre-existing). Toolbar: `profiles.add`, `profiles.bulkCreate`.

## 5. Crypto farm — readiness panel
**Steps (auto):**
1. click `nav.groups`.
2. click `groups.farm.open.Crypto Farm Alpha`.
3. Assert farm panel: summary chip "3/3 ready" (vault unlocked); members
   `farm.member.farm_alpha_01/02/03` each with a masked address + ✓.
**Locked-vault variant:** summary reads "🔒 vault locked — unlock to verify N".
**Pass:** readiness matches vault state.

## 6. Crypto farm — balance sweep (read-only RPC)
**Steps (auto):**
1. open the farm panel (scenario 5).
2. select a chain in `farm.chain` (e.g. Ethereum).
3. click `farm.balances`.
**Expect:** each member row's balance goes "…" → a real value, e.g. `1.2345 ETH`.
No address → "—". Rate-limited public RPC → "rpc error" (graceful, no crash).
Switch `farm.chain` to Polygon/BSC → values + symbol change.

## 7. Crypto farm — selector-override editor
**Steps (auto):**
1. open farm panel → click `farm.selectors`.
2. click `walletSelectors.example` → JSON appears in `walletSelectors.json`.
3. corrupt the JSON → click `walletSelectors.save` → status shows "Invalid JSON…",
   does NOT save.
4. fix → `walletSelectors.save` → closes. Reopen → JSON persisted.
   `walletSelectors.reset` → clears (defaults restored).

## 8. Crypto farm — mass wallet task (full, needs MetaMask)
**Prereq:** MetaMask installed in the member profiles; a script with wallet steps
assigned/available (see §8 example). Vault unlocked; wallets have passwords.
**Steps (auto):**
1. open farm panel → pick a script in `farm.script` → click `farm.run` → confirm.
2. **Expect:** each idle member launches its own Chrome; MetaMask home opens, the
   vault password is typed, wallet unlocks. Run/Refresh/Balances disable while busy.
**Example wallet script:**
```json
[
  { "type": "assert_gas_below", "params": { "chain": "ethereum", "max_gwei": "40" } },
  { "type": "assert_balance",   "params": { "chain": "ethereum", "min": "0.005" } },
  { "type": "wallet_unlock",    "params": { "wallet": "metamask" } },
  { "type": "navigate",         "params": { "url": "https://app.somedapp.xyz" } },
  { "type": "wallet_confirm",   "params": { "wallet": "metamask" } },
  { "type": "record_tx",        "params": { "chain": "ethereum", "hash": "{{tx_hash}}" } },
  { "type": "wait_tx",          "params": { "chain": "ethereum", "hash": "{{tx_hash}}", "timeout_sec": 180 } }
]
```

## 9. Chain pre-flight / tx steps (no own funds needed)
Run a script on a profile using a **public funded address** to exercise the RPC
read path without owning anything:
```json
[
  { "type": "read_balance",    "params": { "chain": "ethereum", "address": "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", "save_as": "bal" } },
  { "type": "log",             "params": { "message": "balance = {{bal}}" } },
  { "type": "assert_balance",  "params": { "chain": "ethereum", "address": "0xd8dA6BF26964aF9D7eEd9e03E53415D37aA96045", "min": "0.001" } }
]
```
**Negative:** `min: "999999"` → `assert_balance` fails (fail-closed). Offline →
`read_balance` sets `unknown`, run continues; `assert_balance` fails with
"could not read balance (RPC error)".

## Where to look
- Logs: `%LocalAppData%\GhostShell\logs\` (`wallet step`, `read_balance`,
  `record_tx`, `wait_tx`, `Farm-run`).
- Tx history: DB table `tx_history` (written by `record_tx`/`wait_tx`).

---

## 6. AutomationId catalog (stable targets)

### Navigation (sidebar) — `nav.<pageKey>`
`nav.overview`, `nav.profiles`, `nav.groups`, `nav.scripts`, `nav.proxy`,
`nav.fingerprint`, `nav.sessions`, `nav.packs`, `nav.vault`, `nav.extensions`,
`nav.competitors`, `nav.scheduler`, `nav.runs`, `nav.queue`, `nav.traffic`,
`nav.settings` (each also has `AutomationProperties.Name` = the label).

### Page roots — `page.<pageKey>`
`page.overview`, `page.profiles`, `page.groups`, `page.vault` (more added as views
are instrumented).

### Overview
`overview.tile.runs`, `overview.tile.successRate`, `overview.tile.profiles`,
`overview.tile.vault`, `overview.tile.traffic`.

### Profiles
`profiles.add`, `profiles.bulkCreate`, `profiles.row.<profileName>`.

### Vault (page + unlock dialog)
`vault.add`, `vault.lock`, `vault.unlock`, `vault.unlock.cta`, `vault.bulkImport`,
`vault.changePassword`; dialog: `vault.passphrase`, `vault.passphrase.confirm`,
`vault.unlock.submit`, `vault.unlock.cancel`.

### Groups / crypto farm
`groups.create`, `groups.farm.open.<groupName>`; farm panel: `farm.close`,
`farm.chain`, `farm.balances`, `farm.selectors`, `farm.refresh`, `farm.script`,
`farm.run`, `farm.member.<profileName>`.

### Wallet selector editor (dialog)
`walletSelectors.json`, `walletSelectors.example`, `walletSelectors.reset`,
`walletSelectors.save`.

> **Convention for new ids:** `nav.*` for navigation, `page.*` for view roots,
> `<page>.<action>` for buttons/inputs, `<page>.row.<identity>` /
> `farm.member.<identity>` for list rows (bind the id to a stable identity so a
> specific row is addressable). Prefer setting `AutomationProperties.AutomationId`
> in XAML; for code-built dialogs use
> `System.Windows.Automation.AutomationProperties.SetAutomationId(el, "...")`.

---

## Appendix A — seeding harness

Drop this as `tests/GhostShell.Tests/_SeedRealDb.cs`, run
`dotnet test --filter FullyQualifiedName~_SeedRealDb`, then delete it (it writes
to the REAL app DB). Close the app first.

```csharp
// using GhostShell.Core.Common; GhostShell.Core.Models; GhostShell.Data.Database;
// GhostShell.Data.Services; Microsoft.Extensions.Logging.Abstractions; Xunit;
// const string Master = "GhostShell-Master-2026!";
// var db = new DatabaseConnection(AppPaths.DatabasePath, NullLogger<DatabaseConnection>.Instance);
// new MigrationRunner(db, NullLogger<MigrationRunner>.Instance).Run();
// var profiles = new ProfileService(db, NullLogger<ProfileService>.Instance);
// var groups   = new ProfileGroupService(db, NullLogger<ProfileGroupService>.Instance);
// var vault    = new VaultService(db, NullLogger<VaultService>.Instance);
// await vault.RefreshStateAsync();
// if (!vault.IsInitialized) await vault.InitializeAsync(Master); else await vault.UnlockAsync(Master);
// // create profiles (Profile{ Name, GroupName="Crypto Farm Alpha", TemplateId, Language, IsReady=true })
// // vault.CreateAsync(new VaultItem{ Name, Kind="crypto_wallet", ProfileName }, new(){["address"]=..,["wallet_password"]=..})
// // groups.CreateAsync("Crypto Farm Alpha", "...", 3, memberNames)
```
The full version (with the public demo addresses + credentials-file writer) lives
in git history; reproduce from there or from this template.

---

## Appendix B — driving via Windows UI Automation

- **FlaUI (recommended for C# tests):** find by AutomationId, e.g.
  `window.FindFirstDescendant(cf => cf.ByAutomationId("nav.vault")).AsButton().Invoke();`
  then `...ByAutomationId("vault.passphrase").AsTextBox().Enter(master);`
- **WinAppDriver / Appium:** `driver.FindElementByAccessibilityId("farm.balances").Click();`
- **Windows-MCP:** Snapshot returns the element tree; target by the element whose
  metadata matches the AutomationId. Use the tree (not Screenshot pixels) for
  assertions on this WPF app.

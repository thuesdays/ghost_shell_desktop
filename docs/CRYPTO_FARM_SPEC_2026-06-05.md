<!--
  SPDX-License-Identifier: MIT
  Copyright (c) 2026 Mykola Kovhanko <thuesdays@gmail.com>
-->
# Crypto-account farms — wallet automation + mass actions

**Date:** 2026-06-05
**Status:** v1 — implemented
**Scope (agreed with user):** all wallets (config-driven, MetaMask as reference),
the `unlock → connect → confirm` popup-driving primitive as the foundation, and a
Farm management UI with one-click mass-run — built now.

---

## 1. Problem & goal

Running a *farm* of crypto accounts means doing the same wallet action across
many browser profiles: unlock the wallet with its password, connect it to a
dApp, and confirm/sign the transaction the dApp asks for — repeated over tens or
hundreds of profiles, unattended.

GhostShell already had the hard parts (anti-detect Chromium, per-profile proxies,
a script engine, a vault, an extension installer, a run queue with a concurrency
cap). The gaps that blocked crypto-farm automation were:

| # | Gap | Impact |
|---|-----|--------|
| G1 | `switch_tab` was a NOOP; the script engine never switched windows | Can't reach a wallet's **separate** confirmation popup window |
| G2 | No "drive the wallet" step | Each user hand-wrote brittle JS per wallet |
| G3 | Extension popup DOMs change every release | Hardcoded selectors rot fast |
| G4 | No farm-level "run this task on all members" + readiness view | Mass actions were manual, one profile at a time |

This spec closes G1–G4.

---

## 2. Architecture overview

```
Core (data, pure)                Runtime (drives Chromium)         App (WPF UI)
─────────────────                ─────────────────────────         ────────────
WalletDescriptor       ┌────────►WalletPopupDriver  ──uses──► IBrowserSession
WalletFlow/FlowStep    │         (find window, run flow,        (GetWindowHandles,
WalletAction (enum)    │          restore focus)                 SwitchToWindow,
CuratedWalletCatalog ──┘              ▲                          TrustedClick/Type)
                                      │ called by
FarmReadiness (pure) ◄──┐        ScriptRunner steps
                        │        wallet_unlock / _connect /
IVaultService           │        _confirm / _approve / wallet_flow
(addr + password) ──────┘        switch_window (G1 fix)
                                      ▲
                                      │ mass-run
GroupsViewModel + GroupsView ─────────┘
("Crypto farm" panel: readiness + Run wallet task across farm)
```

**Design principle — config over code.** Wallet popup selectors live in
`WalletDescriptor` data, not in C# `if`-ladders. Each flow step lists *several*
candidate selectors (`AnyOfSelectors`) and the driver clicks/types the first one
that appears. When a wallet ships a UI change, the fix is a selector edit, not a
recompile. MetaMask is the reference descriptor; OKX / Phantom / Rabby / Backpack
ship as best-effort descriptors the user can tune.

---

## 3. Data model (Core) — `src/GhostShell.Core/Wallets/`

### `WalletAction` (enum)
`WaitFor`, `Click`, `Type`, `Press`, `Delay` — one verb per flow step.

### `WalletFlowStep` (record)
| Field | Type | Meaning |
|-------|------|---------|
| `Action` | `WalletAction` | what to do |
| `AnyOfSelectors` | `string[]` | candidate CSS selectors; first visible wins |
| `Value` | `string?` | for `Type` — literal or `{{vault.PASS}}` placeholder |
| `Key` | `string?` | for `Press` — `Enter`, `Tab`, … |
| `TimeoutMs` | `int` | per-step wait budget (default 15000) |
| `Optional` | `bool` | if true, a miss is skipped, not an error (e.g. "Next" page that may not appear) |
| `DelayMs` | `int` | for `Delay`, or post-action settle |
| `Description` | `string` | human label for logs |

### `WalletFlow` (record)
`Name` (unlock/connect/confirm/approve) + `Steps` (`WalletFlowStep[]`) +
`OpensExtensionPage` (bool — unlock opens `home.html` as a tab; connect/confirm
attach to a wallet-spawned **popup window**).

### `WalletDescriptor` (record)
| Field | Type | Meaning |
|-------|------|---------|
| `Id` | `string` | stable key, e.g. `metamask` |
| `Name` | `string` | display name |
| `Chain` | `WalletChain` | `Evm` / `Solana` / `Multi` |
| `ExtIds` | `string[]` | known 32-char Chrome IDs (popup-URL match) |
| `HomePage` | `string` | ext page to open for unlock (`home.html`) |
| `Flows` | `Dictionary<string,WalletFlow>` | by name |

Helper: `MatchesPopupUrl(string url)` → true when `url` starts with
`chrome-extension://<extId>/` for any of `ExtIds`.

### `CuratedWalletCatalog` (static)
`IReadOnlyList<WalletDescriptor> Entries`, `TryGet(id)`, `MatchByPopupUrl(url)`.
Ships descriptors for **metamask, okx, phantom, rabby, backpack**.

---

## 4. Driver (Runtime) — `src/GhostShell.Runtime/Browser/WalletPopupDriver.cs`

```csharp
public sealed class WalletPopupDriver
{
    Task RunFlowAsync(
        IBrowserSession session,
        WalletDescriptor wallet,
        string flowName,
        Func<string,string> resolveValue,   // {{vault.PASS}} → cleartext
        ILogger? log, CancellationToken ct);
}
```

Behaviour:
1. **Record** the current window handle (origin tab).
2. **Locate the target window:**
   - `OpensExtensionPage` flow → open `chrome-extension://<extId>/<HomePage>` as a
     new tab and switch to it (the toolbar action-popup isn't a Selenium window).
   - Otherwise → poll `GetWindowHandlesAsync`, switch to each, read `location.href`,
     and stop at the first that `wallet.MatchesPopupUrl(url)` (the wallet-spawned
     `notification.html` confirm window). Bounded by the first step's timeout.
3. **Run steps** in order with the trusted primitives:
   - `WaitFor` → poll `document.querySelector(any-of)` until visible/timeout.
   - `Click` → `TrustedClickAsync(firstVisible)` (CDP Input — `isTrusted:true`).
   - `Type` → resolve value, `TrustedTypeAsync(firstVisible, value)`.
   - `Press` → `TrustedPressKeyAsync(key)`.
   - `Delay` → jittered wait.
   - `Optional` miss → log + skip; required miss → `WalletFlowException`.
4. **Finally:** switch back to the origin handle (never yank the user's tab),
   tolerating `NoSuchWindow` (confirm windows self-close).

Window/input are already window-aware (CDP `Input.*` and WebDriver follow
`SwitchTo().Window`), so no new session API is needed.

---

## 5. Script steps (Runtime) — `ScriptRunner.cs`

New cases in `ExecuteStepAsync`:

| Step | Params | Notes |
|------|--------|-------|
| `wallet_unlock` | `wallet` (id, default `metamask`), `password` (default `{{vault.PASS}}`) | opens home page, types password, clicks Unlock |
| `wallet_connect` | `wallet` | drives the connect popup (select-all accounts → Next → Connect) |
| `wallet_confirm` | `wallet` | drives the confirm/sign popup |
| `wallet_approve` | `wallet` | token spend-approval popup |
| `wallet_flow` | `wallet`, `flow` | generic — run any named flow |
| `switch_window` (alias `switch_tab`) | `index` or `url_contains` | **G1 fix** — real window switch |

Password handling: the `password` param is run through `InterpolateVars`
(resolves `{{vault.PASS}}` → the profile's `crypto_wallet.wallet_password`), then
added to `ctx.SecretValues` so it is **redacted from every log line**. Cleartext
is never logged or stored.

---

## 6. Farm UI (App) — Groups page = farms

A *farm* is a `ProfileGroup` (already has members + a `MaxParallel` cap). We add a
crypto layer on top instead of a new entity.

### `FarmReadiness` (pure, `src/GhostShell.Core/Wallets/FarmReadiness.cs`)
Given `(memberName, hasWallet, hasPassword, address)` tuples →
- per-member `FarmMemberStatus { Name, Ready, AddressMasked, Reason }`
- farm summary `{ Total, Ready, NeedsWallet, NeedsPassword }`.
Address masking: `0x1234…ABCD` (never show full address in the list; full on hover).
Fully unit-testable with no I/O.

### `GroupsViewModel` / `GroupsView`
- Selecting a group loads its members + their vault `crypto_wallet` items
  (`IVaultService.ListAsync(kind:"crypto_wallet")` + `GetClearAsync` only for
  address/password presence — addresses are not secret; **passwords are checked
  for presence only, never surfaced**).
- New **"🪙 Crypto farm"** panel: readiness summary chip + per-member rows.
- New command **"Run wallet task across farm"**: pick a script → confirm
  ("assigns the script to N members and starts them, cap = …") → set each
  member's `AssignedScriptId` (persisted) → group start (existing staggered loop).

---

## 7. Security & safety

- **Cookie/secret hygiene:** passwords resolved at run time, added to
  `SecretValues`, redacted in logs; presence-only checks in the farm view.
- **No silent browser kill:** mass-run reuses the existing staggered start that
  skips already-running members.
- **Selector injection:** wallet selectors come from the shipped catalog (trusted
  data), not user free-text in v1; `extension_id` still validated via
  `ScriptSecurityGuards.IsValidExtensionId`.
- **Focus safety:** the driver always restores the origin tab.

---

## 8. Test plan (`tests/GhostShell.Tests/Wallets/`)

| Test | Asserts |
|------|---------|
| `Catalog_HasReferenceWallets` | metamask/okx/phantom/rabby/backpack present, non-empty flows |
| `Catalog_ExtIds_AreValidChromeIds` | every ExtId is 32 chars a–p |
| `MatchesPopupUrl_*` | matches `chrome-extension://<id>/…`, rejects others |
| `MatchByPopupUrl_FindsWallet` | URL → right descriptor |
| `Unlock_Flow_TypesPasswordPlaceholder` | unlock flow has a Type step using `{{vault.PASS}}` |
| `FlowSteps_AllHaveSelectorsOrKey` | no empty/ill-formed steps |
| `FarmReadiness_*` | ready/needs-wallet/needs-password buckets; address masking; summary counts |

Live Chromium driving isn't unit-testable here; the pure descriptor/flow/readiness
logic is covered exhaustively, and the driver is exercised manually + via the
existing extension-step infra.

---

## 9. Files touched

**New**
- `src/GhostShell.Core/Wallets/WalletModels.cs` (descriptor/flow/step/enums)
- `src/GhostShell.Core/Wallets/CuratedWalletCatalog.cs`
- `src/GhostShell.Core/Wallets/FarmReadiness.cs`
- `src/GhostShell.Runtime/Browser/WalletPopupDriver.cs`
- `tests/GhostShell.Tests/Wallets/CuratedWalletCatalogTests.cs`
- `tests/GhostShell.Tests/Wallets/FarmReadinessTests.cs`

**Modified**
- `src/GhostShell.Runtime/Scripts/ScriptRunner.cs` (wallet_* + switch_window)
- `src/GhostShell.App/ViewModels/GroupsViewModel.cs` (farm panel + mass-run)
- `src/GhostShell.App/Views/GroupsView.xaml` (farm panel)
- `docs/` (this spec + feature notes)

---

## 9a. Post-audit fixes (two parallel review passes)

Two independent adversarial reviews ran over the new code; every real finding
was fixed and locked with tests.

**Wallet engine**
- **C2 (critical):** the `{{vault.PASS}}` placeholder lives in the catalog (C#),
  not the script JSON, so the alias collector never pre-resolved it → the literal
  `{{vault.PASS}}` would be typed as the password (wallet lockout). Fixed:
  `CollectVaultAliases` now force-adds `PASS`/`ADDR` when any `wallet_*` step is
  present, **and** the driver fails closed if a value still contains `{{` instead
  of typing it. (tested: `WalletStepAliasTests`)
- **C1 (critical):** explicit `password` override is now redacted unconditionally
  (dropped the `≥3 chars` heuristic) so it can never reach a log line.
- **H1/H2/H3 (high):** window-focus was leaked on the no-match/throw paths and the
  unlock tab was leaked every run. The driver now wraps acquisition + flow in one
  `try/finally` that **always restores the origin tab and closes the unlock tab.**
- **M4 (medium):** the confirm search now prefers the `notification`/confirm popup
  over a lingering home page, and **probes each window at most once** (no more
  flicker / re-entering the user's real tabs every tick — old L5).

**Farm UI**
- **#1 (high):** `LoadFarmAsync` was fire-and-forget and could interleave two
  selections into `FarmMembers`. Now guarded by a monotonic load token
  (build-then-swap; stale loads drop their results).
- **#2 (high):** the N per-member vault decrypts now run **off the UI thread**
  (`Task.Run`), populating the collection in one marshaled batch.
- **#4 (medium):** `ReloadAsync` re-resolves `SelectedGroup` to the fresh row so
  the singleton VM can't hold an orphaned selection across navigations.
- **#5 (medium):** mass-run now **assigns the task to every member first, then
  launches the idle ones** — no half-retasked farm, explicit contract.
- **#6 / #8 (low):** locked vault shows an explicit "unlock to verify" summary
  (no longer mislabels members as "no password"); Run/Refresh disable while busy
  via `CanExecute`.

**Tests:** 462/462 green (38 new across `Wallets/` + `WalletStepAliasTests`).
Clean build, clean boot verified.

## 10. Phase 57 — chain layer (delivered)

The "next iteration" items are now built. **Signing stays in the wallet popup**
(operator choice) — everything here is read-only RPC + persistence + UI.

**Chains & RPC**
- `Core/Chains/`: `ChainDescriptor`, `CuratedChainCatalog` (Ethereum, BSC,
  Polygon, Arbitrum, Base, Optimism, Solana), `ChainUnits` (BigInteger-safe
  hex/unit math — whale-balance safe, no `decimal` overflow).
- `IChainRpcClient` + `Runtime/Chains/JsonRpcChainClient` — EVM
  (`eth_getBalance` / `eth_gasPrice` / `eth_getTransactionCount` /
  `eth_getTransactionReceipt`) and Solana (`getBalance` / `getSignatureStatuses`),
  no web3 dependency, 15 s per-call timeout, JSON-null surfaced (not silent 0).

**Persistence**
- V29 migration `tx_history` (+ `ITxHistoryService`, atomic UPSERT `RETURNING id`).
- `INonceTracker` (KV-backed last-known nonce per chain+address).

**Script steps** (`read_balance` / `assert_balance`, `read_gas` /
`assert_gas_below`, `record_tx`, `wait_tx`): pre-flight gates + tx tracking.
All RPC calls degrade gracefully — `read_*` → "unknown", `assert_*` fail closed,
`record_tx` never kills a run; `wait_tx` treats timeout/unknown as NOT confirmed.

**Farm UI**: chain dropdown + "💰 Balances" sweep (cancellable, off-thread),
per-member balance column, and a "⚙" **wallet selector-override editor**
(`WalletSelectorEditorDialog`) — field-edit popup selectors (validated JSON,
merged via `WalletCatalog`) without a recompile.

**Post-audit fixes** (one deep review pass): RPC errors no longer crash a farm
run (C1); `ChainUnits` overflow/precision fixed via BigInteger (H2/H3); atomic
`record_tx` (H4); JSON-null ≠ 0 balance (M6); `wait_tx` timeout ≠ success (M9);
`assert_gas_below` fails loud on bad config (M7); nonce out-of-range refused
(M5); balance sweep cancellable (M11); tx-hash shape validated (L14).
**507/507 tests green, clean build, clean boot.**

## 11. Still out of scope / next

- Raw programmatic signing (secp256k1/ed25519) — deliberately NOT done; signing
  stays in the wallet popup per operator choice.
- Settings-backed RPC-endpoint override UI (defaults are public RPCs — fine for
  light use; a busy farm will want a paid endpoint).
- A dedicated browsable tx-history view (data + steps exist; UI surface is thin).
- Solana SPL-token reads; atomic multi-call bundles.

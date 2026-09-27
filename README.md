# Improved Villager Inventory Cleanup

An ASKA BepInEx mod that gets villagers to actually clear unneeded items - above
all stale tools - out of their inventories.

## The problem

ASKA villagers have no mechanic for shedding tools. A villager who switches from
builder to miner carries the road maker, hammers, hoe and axe forever, and those
tools are effectively deleted from the settlement's economy.

The cleanup quest does run (51 times in the 0.2.0 test), and it does scan every
tool. The tools are rejected by a gate near the top of
`FSM_CleanupInventory.CleanupInventoryData.CheckItem`, before any ranking
happens:

```text
if (!_CheckContainerNeedingSpace(item.container) && !dropWithoutReasonItems.Check(item.info))
    return;   // never a candidate
```

`dropWithoutReasonItems` comes from the authored `itemsToDropWithoutReason`
tables, which list materials only. So a tool is only ever a candidate when its
bag is full.

This was read from the game binary with the harness's `scripts/disasm.ps1`.
Some earlier conclusions turned out to be wrong:

- `CheckItem` **always returns false**. Its pick is written to `itemToDrop`,
  so `accepted=False` in the 0.1.0 and 0.2.0 logs meant nothing.
- 0.2.0 forced `depositAny` on, but `CheckItem` only reads that flag when
  `onlyAllowDepositingToStorage` is true, and it was false on every check. The
  0.2.0 fix did nothing.

## What 0.3.0 changes

- **Tools get past the gate.** For items in `EligibleCategories` (default
  `Tools`), the mod makes `_CheckContainerNeedingSpace` answer true for that one
  call. `CheckItem` is its only caller, and the answer feeds only this gate.
  Everything after it is vanilla: `neverDropFilter`, and the `NicePriority`
  ranking for equipped gear, bags, warrior weapons and job-needed items.
- **Only truly unneeded tools are picked.** An admitted tool is kept as the pick
  only if vanilla ranked it 0 ("no reason to keep"). Anything vanilla would
  protect, such as `NEEDED_AT_WORK` or `EQUIPPED_EQUIPMENT`, is reverted.
- **`depositAny` stays forced on.** It is read once after the scan, and routes
  the pick to "deposit into storage" instead of "drop next to a storage".
- **Eye of Odin drops (test mode).** Cleaned-up items are dropped on the ground
  in front of the Eye of Odin (the settlement core) instead of going into
  storage. This proves cleanup works whatever the storage situation. It reuses
  vanilla's "no storage found, walk somewhere and drop it" path, and only changes
  the destination.

## What 0.4.0 changes

The 0.3.0 test showed that the core fix works: stale tools were deposited, and
job tools were kept. It also showed three problems, all addressed here:

- **Force mode now takes effect.** The cleanup FSM first asks the villager's
  own workstation storage for a slot (`StorageToDropIntoPredicate`), and only
  then searches the settlement (`ResourceStoragePredicate`). In force mode both
  predicates now refuse, so every pick goes down the ground-drop path. 0.3.0
  only blocked the settlement search, so everything went into workstation
  storage.
- **The redirect no longer depends on hooking the FSM update.**
  `CleanupWatcher` reads each villager's cleanup state once per frame. It logs
  every decision (`cleanup_state`), redirects ground drops to the Eye, and
  reports villagers stuck on one item for 30 s (`cleanup_stalled`).
- **"Clean inventory now" button.** It appears at the top of the screen while a
  villager's menu is open. Clicking it does exactly what the game does on a job
  change: it sets `CleanupRequested` and `Important`, then calls
  `QuestRunner.ReevaluateQuest`. This is needed because vanilla only asks for a
  cleanup on a job, schedule or viking-status change, and an interrupted run
  loses its request.

## Configuration

| Section | Key | Default | Purpose |
| --- | --- | --- | --- |
| Cleanup | `EnableToolDepositing` | `true` | The core fix. False restores vanilla. |
| Cleanup | `EligibleCategories` | `Tools` | Comma-separated category paths the fix applies to. |
| Cleanup | `AllowGroundDrops` | `true` | Allow ground drops during trash-triggered cleanups. |
| EyeOfOdin | `ForceEyeOfOdinDrops` | `true` | **Test mode.** Skip storage, drop everything at the Eye. |
| EyeOfOdin | `DropAtEyeOfOdinWhenNoStorage` | `true` | When no storage has room, drop at the Eye instead of the workstation. |
| EyeOfOdin | `StandOffDistance` | `4` | Metres in front of the Eye to stand. Negative if they end up behind it. |
| TestTools | `ShowCleanupButton` | `true` | Show the "Clean inventory now" button in the villager menu. |
| TestTools | `CleanupButtonX` / `CleanupButtonY` | `0.5` / `0.04` | Button position, as fractions of screen width and height. |

Turn `ForceEyeOfOdinDrops` off once the test has confirmed that cleanup works.

## Build and deploy

From the `Aska_Mods` harness:

```powershell
.\scripts\deploy.ps1 -Mod Improved_Villager_Inventory_Cleanup
```

`mod.json` targets the `Test_Mods` profile.

## Diagnostic output

Each game launch replaces this focused log:

```text
%APPDATA%\Thunderstore Mod Manager\DataFolder\ASKA\profiles\Test_Mods\BepInEx\config\ImprovedVillagerInventoryCleanup\diagnostic-latest.log
```

The same events are also written to BepInEx's `LogOutput.log`, tagged
`IVIC_DIAG`. See [docs/DIAGNOSTIC_TEST.md](docs/DIAGNOSTIC_TEST.md) for what
each event means.

## Status

The `0.3.0` core fix is validated in-game: tools were deposited and job tools
kept. See `Aska_Mods/logs/Improved_Villager_Inventory_Cleanup/README.md`, run 3.
`0.4.0` (force mode, watcher and button) has not been tested yet.

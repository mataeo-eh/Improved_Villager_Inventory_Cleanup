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

## Configuration

| Section | Key | Default | Purpose |
| --- | --- | --- | --- |
| Cleanup | `EnableToolDepositing` | `true` | The core fix. False restores vanilla. |
| Cleanup | `EligibleCategories` | `Tools` | Comma-separated category paths the fix applies to. |
| Cleanup | `AllowGroundDrops` | `true` | Allow ground drops during trash-triggered cleanups. |
| EyeOfOdin | `ForceEyeOfOdinDrops` | `true` | **Test mode.** Skip storage, drop everything at the Eye. |
| EyeOfOdin | `DropAtEyeOfOdinWhenNoStorage` | `true` | When no storage has room, drop at the Eye instead of the workstation. |
| EyeOfOdin | `StandOffDistance` | `4` | Metres in front of the Eye to stand. Negative if they end up behind it. |

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
`IVIC_DIAG`. New in 0.3.0: `tool_admission_selected`,
`tool_admission_reverted`, `eye_of_odin_resolved`, `eye_of_odin_redirect` and
`eye_of_odin_unavailable`. See [docs/DIAGNOSTIC_TEST.md](docs/DIAGNOSTIC_TEST.md).

## Status

`0.3.0` has not yet been validated in-game.

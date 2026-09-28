# Improved Villager Inventory Cleanup

An ASKA BepInEx mod that gets villagers to put away anything their current job
does not need: old tools, leftover materials, spare stone. They store it in any
storage that will take it, and drop it at the Eye of Odin (or their outpost)
only as a last resort.

> **EXPERIMENTAL (0.6.1).** Published on Thunderstore for wider testing. If you
> hit a problem, please leave a comment on the Thunderstore page or open an
> issue here, and say which villager job and which item were involved.

## What it does

- **Anything unneeded is cleaned, not just tools.** A stone cutter moved to
  another job puts his small stones away. The game's own check,
  `IsItemNeededByVillager`, decides what the job needs, and that is always kept.
  By default food, water and smilk, bags, clothing, weapons and torches are
  never touched, since villagers need those to survive, dress, defend
  themselves or see at night.
- **An equipped tool is cleaned if the job does not need it.** For example, a
  builder turned woodcutter no longer keeps the hammer in hand. This does not
  apply to warriors.
- **Cleanup keeps going until the villager is done.** Vanilla asks for a
  cleanup only on a job or schedule change, and forgets the request as soon as
  the run ends or is interrupted. The mod re-checks every villager on a timer
  and asks again while they still carry something unneeded. A villager whose
  cleanup cannot remove anything is checked less and less often, so nobody
  loops.
- **Storage rules are respected.** A cleaning villager never puts an item into
  a full container, never takes a warehouse slot past its task quantity (0
  means never), and never uses a slot whose task priority is None
  (`Behaviour/StorageRules.cs`). A building's own storage only takes items that
  building has a task for, or needs from this villager. The vanilla cleanup checks none of these on
  the workstation's own storage, and neither space nor priority in the
  settlement search. The 0.5.0 test showed tools going onto full racks and
  knocking another item off.
- **Storage first, anywhere in the settlement.** The storage search radius is
  lifted, so any storage that accepts the item counts.
- **Last resort: the Eye of Odin.** If no storage anywhere takes the item, the
  villager drops it in front of the Eye of Odin. Villagers who live at an
  outpost drop it in front of their outpost instead.
- **Altar keepers clean up too.** The fire and air altars (`Praystation`), like
  any station that has no cleanup quest of its own, get the game's standard
  one for their workers.
- **Karvi voyages are left alone.** A crew whose ship is at sea or deployed on
  a trip behaves exactly as in vanilla.

## Why vanilla never cleans tools

This was read from the game binary with the harness's `scripts/disasm.ps1`.
Near the top of `FSM_CleanupInventory.CleanupInventoryData.CheckItem`:

```text
if (!_CheckContainerNeedingSpace(item.container) && !dropWithoutReasonItems.Check(item.info))
    return;   // never a candidate
```

`dropWithoutReasonItems` lists materials only, so a tool is a candidate only
when the villager's bag is full. The mod lets eligible items past this gate.
Everything after the gate is still vanilla ranking (`NicePriority`), and the
mod only keeps a pick the game ranked 0 ("no reason to keep"), or an equipped
tool the job does not need. The source files' headers explain each piece.

## Configuration

| Section | Key | Default | Purpose |
| --- | --- | --- | --- |
| Cleanup | `EnableImprovedCleanup` | `true` | Master switch. False restores vanilla. |
| Cleanup | `EligibleItemCategories` | `*` | Categories that may be cleaned. `*` means all. |
| Cleanup | `ExcludedItemCategories` | food, elements, bags, armor, weapons, torches | Never cleaned. |
| Cleanup | `CleanEquippedTools` | `true` | Clean an equipped tool the job does not need. |
| Cleanup | `KeepCleaningUntilDone` | `true` | Re-request cleanup while unneeded items remain. |
| Cleanup | `RecheckIntervalSeconds` | `20` | How often villagers are re-checked. |
| Cleanup | `AddCleanupToStationsWithout` | `true` | Give altar workers, and similar, a cleanup quest. |
| Cleanup | `StorageSearchDistance` | `100000` | Storage search radius. `0` keeps the game's own. |
| LastResort | `DropAtEyeOfOdinWhenNoStorage` | `true` | Drop at the Eye of Odin or outpost when no storage takes an item. |
| LastResort | `StandOffDistance` | `4` | Metres in front of the Eye or outpost. |
| Debug | `ForceLastResortDrops` | `false` | Skip storage; send everything to the drop point. |
| Debug | `ShowCleanupButton` | `false` | "Clean inventory now" button in the villager menu. |
| Debug | `CleanupButtonX` / `CleanupButtonY` | `0.5` / `0.15` | Button position, as fractions of the screen. |

Category names are matched loosely: `Tools`, `Resources/Stone` and so on, case
and spaces ignored. The categories seen so far are Tools, Weapons, Armor, Bags,
Resources (Food, Elements, Wood, Stone, Materials, Seeds, Misc, Junk, Iron,
Magic) and Blueprints.

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

## Status and history

| Version | Result |
| --- | --- |
| 0.2.0 | Did nothing: it forced a flag the gate above never reads. |
| 0.3.0 | Core fix validated: stale tools deposited, job tools kept. |
| 0.4.0 | Forced Eye of Odin drops and the debug button validated in-game. Altar workers had no cleanup quest. Clearing everything needed several requests. |
| 0.5.0 | Largely working in-game: automatic cleanup, all item types, altar keepers. Tools were put onto full racks, and warehouse task limits were ignored. |
| 0.6.0 | Storage rules for space, task quantity and task priority. First Thunderstore release, experimental. **Broke the settlement storage search for all villagers**: Il2CppInterop cannot trampoline `FindStorageToDeposit` or `FindTrashcanToDeposit` (by-ref `Vector3&`/`Single&` parameters). |
| 0.6.1 | Removes those hooks. Building storages follow their workstation's tasks (a workshop's tool storage took tools it had no task for). |

Thunderstore package files (manifest, player-facing README, changelog, icon)
are in `packaging/`. Remove the experimental notice there, and in this file,
for 1.0.

The test logs are archived in `Aska_Mods/logs/Improved_Villager_Inventory_Cleanup/`.

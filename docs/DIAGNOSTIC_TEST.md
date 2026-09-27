# Villager inventory cleanup test

## Test for 0.4.0 (current)

The core fix was confirmed in the 0.3.0 run (see the log archive README, run
3). This test checks the Eye of Odin force mode and the new button.

1. Launch ASKA through the `Test_Mods` profile and load the world.
2. Talk to a villager who carries tools their job does not use, to open their
   villager menu.
3. Click **Clean inventory now** at the top of the screen. It should say
   "Cleanup requested for <name>." Close the menu.
4. Follow that villager. Expected: they walk to the Eye of Odin and drop their
   stale tools, and any surplus materials, on the ground in front of it.
5. Repeat with a couple of other villagers. Click again if a run gets
   interrupted: an interrupted run drops its request.
6. Exit normally, then collect `diagnostic-latest.log` before launching again.

Results to check in the log:

| Event | Meaning |
| --- | --- |
| `manual_cleanup_requested` | The button worked. `quest_priority` should be above 0, and `active_quest_after` shows whether cleanup took over at once. |
| `manual_cleanup_failed` | The button found no cleanup quest for that villager. `reason=` says why. |
| `cleanup_state` | Each decision: `outcome=deposit` (into storage), `ground_drop` (walk and drop), or `nothing`. It also gives `walk_target` and `redirected_to_eye`. |
| `cleanup_stalled` | The same item and destination for 30 s. It includes the walk target and villager positions, to tell an unreachable spot apart from an interrupted run. |
| `eye_of_odin_resolved` | Shows what the mod thinks the Eye of Odin is (`core_name`). Check that this really is the Eye. |
| `tool_admission_selected` | A stale tool got past the gate and was picked. **This is the core fix working.** |
| `tool_admission_reverted` | A tool got past the gate but vanilla ranked it as needed, so it was kept. Expected for job tools. |
| `eye_of_odin_redirect` | A villager was sent to the Eye with an item. |
| `eye_of_odin_unavailable` | No settlement core was found for that villager. |

These outcomes mean something is wrong:

- **The villager's current job tool is dropped.** Set `EnableToolDepositing=false` and report it.
- **`cleanup_stalled` appears, or `tool_admission_selected` does but the item never leaves the inventory.**
  The FSM's walk or drop step is the problem. Compare `walk_target_position` with
  `villager_position` in the stall line.
- **Villagers stand beside the Eye without dropping anything.** The stand point
  may be unreachable. Change `StandOffDistance`, for example to 6 or -4.
- **A builder drops items where they stand.** This is a known vanilla branch for
  some Buildstation villagers. It is harmless.

## History: 0.2.0

`0.2.0` is the first build that changes behaviour. It makes items vanilla would
never consider - above all tools a villager no longer needs - eligible for
cleanup. It changes nothing else.

## What 0.1.0 established

The diagnostic run proved the cleanup pipeline is healthy and that storage
permissions were never the blocker:

- cleanup quests did start for the reassigned villager, four times;
- 186 storage whitelist checks returned `allowed=True`, and 30 resource storages
  returned `accepted=True`;
- a crafting material (`Rope`) was selected, carried to storage and deposited;
- every tool finished every scan with `itemToDropPriority=NO_ITEM` - never a
  candidate, not merely an outranked one;
- `deposit_any=False` on every check, because ASKA's authored
  `itemsToReturnToStorages` table lists materials only.

It also ruled scheduling out as a cause. The two sub-second runs each scanned the
full inventory (12 and 19 items) and accepted nothing, so they ended for lack of
anything eligible - not for lack of priority.

## Test for 0.2.0

1. Launch ASKA modded through Thunderstore using the `JustMe` profile.
2. Pick a villager whose name you can remember, and note it down.
3. Give that villager several tools unrelated to their job - ideally a mix of
   early-game ones (wooden hammer, wooden hoe, stone axe) and metal ones
   (crude iron axe, roadmaker), so both the ground-drop and deposit paths get
   exercised.
4. Assign them to a substantially different workstation.
5. Let them fetch the new job's equipment and start one normal task.
6. Keep watching through at least one full leisure period.
7. Exit the world or game normally so the log is flushed.

Do not launch the game twice before collecting the result: the focused log is
replaced at every launch.

## Result

A run in progress writes to the live profile:

```text
%APPDATA%\Thunderstore Mod Manager\DataFolder\ASKA\profiles\JustMe\BepInEx\config\ImprovedVillagerInventoryCleanup\diagnostic-latest.log
```

**The 0.1.0 and 0.2.0 runs described here have already been collected**, and are
archived outside the profile so they survive the mod being uninstalled:

```text
Aska_Mods/logs/Improved_Villager_Inventory_Cleanup/
```

Read the `README.md` there first - it indexes both runs, records what 0.2.0
actually showed, and gives grep recipes for files too large to open whole. The
mod is currently not deployed; re-deploy it before expecting a new
`diagnostic-latest.log`.

What to look for:

- `cleanup_fsm_check_item ... accepted=True` on a tool - the core fix working;
- `item_to_drop` naming something other than `Rope` - previously never happened;
- tools leaving the inventory across successive `periodic_snapshot` lines;
- `category=` and `tier=` on every logged item. These are new in 0.2.0 and are
  what the roadmap's "drop it if a higher tier exists" rules need. Collecting
  the real category names is a goal of this run in its own right.

Signs the change went too far, both of which mean setting `EnableToolDepositing`
to false in the `Cleanup` config section and reporting what happened:

- a villager depositing or dropping the tool required by their current job;
- food, water, bags or worn clothing being put away.

If cleanup still does nothing, the next question is whether `CheckItem` is now
accepting tools but no storage will take them. `cleanup_storage_candidate` lines
carry `storage_check`, `can_store_type` and `remaining_capacity` and will show
that directly - which is a different problem from this one.

## Tracking scope in 0.2.0

`0.1.0` only observed villagers within a window of a workstation change, which
made the most interesting case invisible: a villager cleaning up WITHOUT being
reassigned. Tove did exactly that at `realtime=739.960`, starting a cleanup quest
with `cleanup_requested=False trash_detected=True` after picking up a scrap Iron
Plate - and none of the run that followed was recorded.

Starting a cleanup quest is now itself a reason to begin observing. Any villager
whose cleanup quest starts is tracked for `CleanupTrackingWindowSeconds`
(default 180), so the whole run gets captured.

Two consequences worth knowing when reading the next log:

- item classifications that happened BEFORE an untracked villager's quest started
  are still missing, because `_IsTrash` runs ahead of `Start()`. The quest start
  itself and everything after it is captured;
- more villagers being tracked means a larger log. Lower
  `CleanupTrackingWindowSeconds` if it becomes unwieldy.

The `0.1.0` run was originally preserved next to the live log as
`diagnostic-0.1.0-baseline.log`, since only `diagnostic-latest.log` is replaced
at launch. Both runs now live in the archive named under **Result** above, as
`2026-09-20_v0.1.0_baseline.log` and `2026-09-20_v0.2.0_tool-depositing.log`.

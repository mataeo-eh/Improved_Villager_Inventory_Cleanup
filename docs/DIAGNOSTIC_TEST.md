# Villager inventory cleanup test

## Test for 0.6.2 (current)

1. Launch the `Test_Mods` profile and open a villager's menu. The cleanup button
   should be visible on the first 0.6.2 launch even if the old debug setting was false.
2. Press F7 once to hide the button, then again to show it. Close the menu and
   toggle F7; reopen the menu to confirm it also works while the menu is closed.
   Relaunch with it hidden to check the saved `[UI] ShowCleanupButton` value.
3. Change `[UI] CleanupButtonToggleKey` to another key before launching (or in
   a config editor); check that only the new key toggles it. `None` disables it.
4. Give a worker several unneeded tools and a stack of spare materials. Request
   cleanup once. After each successful pass, they should start the next without
   a 10-20 second detour to work. A partial-stack deposit should continue too.
5. Look for `cleanup_pass_finished progress=True continue_now=True retry_seconds=0`
   followed promptly by another `cleanup_quest_started` for that villager.
6. A pass that removes nothing should log `continue_now=False`, with retries
   backing off to at most 600 seconds. Change job during a pass and check that
   the old cleanup does not immediately restart. Urgent needs should still win.
7. Set `KeepCleaningUntilDone=false`, then `EnableImprovedCleanup=false` to check
   that immediate continuation stops. Restore the defaults afterwards.
8. Repeat the storage checks below and check normal haulers and voyage crews.

`cleanup_button_toggled` records visibility and the key. `cleanup_pass_finished`
records completion status, remaining item stacks, progress, and whether the
next pass was requested immediately. Completed clean inventories need no retry.

## Test for 0.6.1

Repeat the workshop test: give a workshop villager unrelated tools, request
a cleanup, and check the workshop's tool storage only receives tools the
workshop has a task for. Look for `storage_refused reason=station_has_no_task_for_item`.
Also check that haulers work normally. 0.6.0 broke their storage search: look
in `LogOutput.log` for "During invoking native->managed trampoline", which
should no longer appear.

## Test for 0.6.0

0.6.0 adds the storage rules. Watch a warehouse whose tool racks are full, or
have task quantities and priorities set, and check that no cleaning villager
puts a tool there. Items no storage accepts should go to the Eye of Odin.
`storage_refused` lines give the reason each storage was refused: `full`,
`task_quantity`, `task_priority_none` (with the raw priority number) or
`no_task_for_item`.

## Test for 0.5.0

0.4.0 proved cleanup works and that villagers reach the Eye of Odin. 0.5.0
makes it automatic and general. Storage comes first, and the Eye of Odin (or
their outpost) is the last resort. Force mode is off.

1. Launch ASKA through the `Test_Mods` profile and load the world. **Do not
   press the button at first.** The point is to see cleanup happen on its own.
2. Watch a few villagers you know carry unneeded tools or materials, including
   a fire or air altar keeper. Within a minute or two they should walk to a
   storage and put the items away, and keep going until they are done.
3. Change a stone cutter (or any worker with job materials) to another job.
   They should put the old job's materials away.
4. If you can, have a karvi crew on a voyage. They should behave exactly as in
   vanilla.
5. Use the debug button (enabled in the Test_Mods config) only if a villager
   never starts on their own. Note who it was.
6. Exit normally, then collect `diagnostic-latest.log` before launching again.

Results to check in the log:

| Event | Meaning |
| --- | --- |
| `unneeded_items_found` | The scheduler saw unneeded items and requested a cleanup. It lists the items, whether the last request made progress, and when the next check is. |
| `cleanup_requested` | A cleanup was requested (`reason=unneeded_items` or `button`), with its priority. |
| `cleanup_quest_added` / `cleanup_quest_removed` | A worker of a station without a cleanup quest (altars) was given one, or it was taken away after a job change. |
| `item_admission_selected` | An unneeded item got past the vanilla gate and was picked. |
| `item_admission_reverted` | The game ranked the item as needed (for example `NEEDED_AT_WORK`), so it was kept. |
| `cleanup_state` | Each decision: `deposit` (into storage), `ground_drop` (no storage took it), or `nothing`. |
| `drop_point_redirect` | Last resort: a villager was sent to the Eye of Odin or their outpost. Each one of these means no storage anywhere accepted the item. |
| `drop_point_resolved` | Which structure was used (`kind=eye_of_odin` or `outpost`). |
| `voyage_state_changed` | A villager left for (`away=True`) or returned from a voyage. |
| `storage_search_distance_set` | The cleanup storage radius was lifted. `authored=` is the game's original value. |
| `cleanup_stalled` | The same item and destination for 30 s: usually a long walk, sometimes an unreachable spot. |

These outcomes mean something is wrong:

- **A villager puts away something their job uses** (a farmer's seeds, a
  firekeeper's wood). Report the item and job. The quick fix is to add the
  category to `ExcludedItemCategories`.
- **A villager keeps starting and stopping cleanup with no items leaving.**
  Look for repeated `unneeded_items_found` with `progress=False`. The back-off
  should stretch the checks out to 10 minutes.
- **A voyage crew drops items or walks home.** Check that `voyage_state_changed
  away=True` was logged for them.

## History: 0.4.0

The 0.4.0 test (force mode, button) confirmed that the button works and that
villagers drop at the Eye of Odin. It also found that altar workers
(`Praystation`) have no cleanup quest at all, and that clearing a villager
fully took several requests.

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

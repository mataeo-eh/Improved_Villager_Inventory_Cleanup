# Improved Villager Inventory Cleanup

> **EXPERIMENTAL.** This mod works in testing but is not yet polished. If you run
> into problems, such as a villager putting away something they need or behaving
> oddly, please leave a comment on the Thunderstore page or open an issue on
> [GitHub](https://github.com/mataeo-eh/Improved_Villager_Inventory_Cleanup/issues).
> Say what the villager's job was and what item was involved.

In vanilla ASKA, villagers never put tools away. Change a builder into a miner
and they carry the hammer, hoe, axe and road maker forever, and those tools are
effectively lost to your settlement.

With this mod, villagers behave like normal villagers, just a bit smarter about
their inventory:

- **They put away what their current job does not need**, including old tools,
  leftover materials and spare stone. Anything the job uses is kept. So are food,
  water, bags, clothing, weapons and torches.
- **They respect your storages.** They never put an item into a full container.
  In a warehouse they follow each slot's task settings, so an item is not placed
  beyond its quantity limit, or where its priority is set to None. A building's
  own storage (a workshop's tool storage, a woodcutter's stick pile) only takes
  items that building has a task for, or needs itself.
- **They keep at it until they are done**, instead of forgetting after one item.
  After a successful pass they immediately request another if unneeded items
  remain, without waiting for the periodic inventory check. Normal quest
  priorities still apply. A pass that removes nothing waits longer before retrying.
- **Clean inventory now:** open a villager's menu to request a cleanup manually.
  The button is on by default. Press **F7** to show or hide it; your choice is saved.
- **Last resort only:** if no storage anywhere will take an item, they drop it in
  front of the Eye of Odin, or in front of their outpost if they live at one, so
  you always know where to find it.
- **Tool in hand:** a tool the villager is holding is put away too, if the new job
  does not need it. This does not apply to warriors.
- **Fire and air altar keepers** clean up as well.
- **Karvi crews at sea or deployed on a trip are left completely vanilla.**

## Configuration

`BepInEx/config/aska.improved.villager.inventory.cleanup.cfg`

| Section | Key | Default | What it does |
| --- | --- | --- | --- |
| Cleanup | `EnableImprovedCleanup` | `true` | Master switch. False is pure vanilla. |
| Cleanup | `EligibleItemCategories` | `*` | Item categories that may be put away. |
| Cleanup | `ExcludedItemCategories` | food, water, bags, armor, weapons, torches | Never put away. |
| Cleanup | `CleanEquippedTools` | `true` | Put away a held tool the job does not need. |
| Cleanup | `KeepCleaningUntilDone` | `true` | Immediately continue successful passes until nothing unneeded is left. |
| Cleanup | `RecheckIntervalSeconds` | `20` | Background checks and fallback retries; successful passes continue immediately. |
| Cleanup | `StorageSearchDistance` | `100000` | How far to look for storage. `0` keeps the game's own limit. |
| LastResort | `DropAtEyeOfOdinWhenNoStorage` | `true` | Drop at the Eye of Odin or outpost when no storage takes an item. |
| LastResort | `StandOffDistance` | `4` | Metres in front of the Eye or outpost. |
| UI | `ShowCleanupButton` | `true` | Show the manual cleanup button in the villager menu. |
| UI | `CleanupButtonToggleKey` | `F7` | Show/hide button hotkey. `None` disables it. |

F7 saves the button's visibility for future launches and leaves automatic
cleanup running. Edit the config before launching, or use an in-game config
editor. When upgrading, the old `[Debug] ShowCleanupButton` setting is ignored;
the new `[UI]` setting defaults to on.

If villagers put away something you would rather they kept, add its category to
`ExcludedItemCategories`, for example `Resources/Seeds`.

## Notes

- Existing saves are fine. Villagers already carrying old tools will clear them
  over the first few minutes.
- Multiplayer has not been tested.
- A diagnostic log is written to
  `BepInEx/config/ImprovedVillagerInventoryCleanup/diagnostic-latest.log`, and
  replaced each launch. Attaching it to a problem report helps a lot.

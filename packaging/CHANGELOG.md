# Changelog

## 0.6.3

- After a cleanup pass removes nothing, the mod stops automatically requesting
  another cleanup for that villager. The game may still request one on its own,
  and the manual button still works.
- If a later game-triggered or manual pass removes an item, immediate
  continuation resumes while unneeded items remain.

## 0.6.2

- The "Clean inventory now" button is on by default. F7 shows or hides it in
  game and saves your choice. Configure `[UI] ShowCleanupButton` and
  `CleanupButtonToggleKey` to change visibility or the hotkey.
- Successful cleanup passes immediately request the next pass while unneeded
  items remain, instead of waiting for the 20-second inventory check. Partial
  stack deposits count as progress too. Normal quest priorities still apply.
- Passes that remove nothing back off; interruptions and job changes do not
  trigger an immediate restart.

## 0.6.1

- **Important fix, please update.** 0.6.0 broke the game's settlement-wide
  storage search for all villagers, haulers included, not just cleaning ones.
  0.6.1 no longer touches that search.
- Building storages now follow their workstation's tasks, not just warehouses.
  A cleaning villager only puts an item into a workshop's tool storage, a
  woodcutter's stick pile and so on if that building has a task for the item
  (within its quantity and priority), or the building itself needs it.

## 0.6.0

First public release (experimental).

- Villagers put away tools and items their current job does not need, and keep
  at it until they are done.
- Storage rules are respected. A cleaning villager never puts an item into a
  full container, and in a warehouse follows each slot's quantity limit and
  priority (None means not here).
- Last resort: an item no storage will take is dropped in front of the Eye of
  Odin, or in front of the villager's outpost.
- Fire and air altar keepers clean up too.
- Karvi crews away on a voyage are left fully vanilla.

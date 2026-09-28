# Changelog

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

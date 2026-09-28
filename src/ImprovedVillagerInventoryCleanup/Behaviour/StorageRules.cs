// StorageRules - the rules every storage must pass before a cleaning villager
// may put an item into it.
//
// Role in the larger system
// -------------------------
// The 0.5.0 test showed villagers putting tools onto full tool racks (an item
// already on the rack was pushed out) and ignoring the warehouse task limits.
// Read from the game binary, the cleanup's two storage checks are thin:
//
//   CleanupInventoryData.StorageToDropIntoPredicate  - the villager's own
//       workstation storage. Checks only the interaction type and whether the
//       container accepts the item type. No space check, no task check. The
//       0.5.0 log shows it accepting racks with remaining_capacity=0.
//   Settlement.FindStorageToDeposit (first cleanup call) - per warehouse task,
//       only "task quantity > items already there". No space check, and no
//       priority check.
//
// Vanilla almost never reaches these with tools, because vanilla cleanup never
// picks tools. This mod does, so it must hold the cleanup to the rules a
// player expects. A storage is allowed only when ALL of these hold:
//
//   1. Space: the container has room for the whole stack (ItemContainer.HasSpace).
//   2. Task quantity: if the storage is a warehouse slot with a task for this
//      item, the items already there plus this stack stay within the task's
//      quantity. A quantity of 0 means never.
//   3. Task priority: that task's priority is not None. The game stores
//      WorkstationTaskPriority as High=0, Med=1, Low=2, None=3 (read from the
//      interop assembly), so a raw 0 means High, not "none".
//   4. A warehouse slot with tasks but no task for this item is refused, as
//      a hauler would not put the item there either.
//
// Storages that are not warehouse task slots (a chest, a station's own input)
// only need rule 1. When every storage refuses, the item goes to the last-resort
// drop point (Eye of Odin or outpost).
//
// The task index maps each storage interaction to its tasks. It is rebuilt from
// every ResourceStorage in the world on the scheduler's sweep, and on demand
// (at most every 2 s) when an unknown storage is checked. Task objects are read
// live, so a limit the player just changed applies at once.
//
// Depends on: DiagnosticLog, DiagnosticTracker, GameDescribe.

using System;
using System.Collections.Generic;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class StorageRules
{
    private const int PriorityNone = 3;   // WorkstationTaskPriority.None
    private const float RebuildThrottleSeconds = 2f;

    // storage interaction pointer -> the warehouse tasks that fill it.
    private static Dictionary<IntPtr, List<ResourceStorageTaskData>> _tasksByInteraction = new();
    private static readonly HashSet<IntPtr> KnownUntasked = new();
    private static float _lastRebuildAt = -100f;

    // Refusals already logged ("villager|storage|item|reason"), so a villager
    // re-checking the same rack every scan does not flood the log.
    private static readonly HashSet<string> LoggedRefusals = new();

    /// <summary>
    /// True when <paramref name="item"/> may be deposited into
    /// <paramref name="storage"/> under the rules in the file header.
    /// Never throws; an unreadable storage is refused.
    /// Called by: the storage patches in CleanupBehaviourPatches.
    /// </summary>
    /// <param name="storage">The storage interaction the villager would use.</param>
    /// <param name="item">The item being put away.</param>
    /// <param name="villager">The villager, for the log.</param>
    internal static bool Allows(StorageInteraction storage, Item item, Villager villager)
    {
        if (storage == null || item?.info == null) return false;
        try
        {
            var info = item.info;
            var count = Math.Max(1, item.count);
            var container = storage.Container;
            if (container == null) return Refuse(villager, storage, item, "no_container");

            // Rule 1: room for the whole stack.
            if (!container.HasSpace(info, count))
                return Refuse(villager, storage, item, $"full remaining={container.GetRemainingCapacity(info)}");

            var tasks = TasksFor(storage);
            if (tasks == null) return true;   // not a warehouse slot: space is enough

            var task = FindTask(tasks, info);
            if (task == null) return Refuse(villager, storage, item, "no_task_for_item");

            // Rule 3: priority None means "do not bring this here".
            var priority = task.priority;
            if (priority >= PriorityNone || priority < 0)
                return Refuse(villager, storage, item, $"task_priority_none raw_priority={priority}");

            // Rule 2: stay within the task's quantity (0 = never).
            var limit = task.itemInfoQuantity?.quantity ?? 0;
            var existing = container.GetItemCount(info);
            if (existing + count > limit)
                return Refuse(villager, storage, item, $"task_quantity existing={existing} adding={count} limit={limit}");

            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("storage_rules_failed", exception);
            return false;
        }
    }

    /// <summary>
    /// Rebuilds the task index from every warehouse in the world.
    /// Called by: CleanupScheduler.StartSweep, and TasksFor on a miss.
    /// </summary>
    internal static void RebuildIndex()
    {
        _lastRebuildAt = Time.realtimeSinceStartup;
        var index = new Dictionary<IntPtr, List<ResourceStorageTaskData>>();
        try
        {
            foreach (var warehouse in UnityEngine.Object.FindObjectsOfType<ResourceStorage>())
            {
                var taskDatas = warehouse?._taskDatas;
                if (taskDatas == null) continue;
                for (var index2 = 0; index2 < taskDatas.Count; index2++)
                {
                    var task = taskDatas[index2]?.TryCast<ResourceStorageTaskData>();
                    var interaction = task?.storageSupply?.interaction;
                    if (interaction == null) continue;

                    var key = interaction.Pointer;
                    if (!index.TryGetValue(key, out var list))
                    {
                        list = new List<ResourceStorageTaskData>();
                        index[key] = list;
                    }
                    list.Add(task);
                }
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("storage_task_index_failed", exception);
        }
        _tasksByInteraction = index;
        KnownUntasked.Clear();
    }

    /// <summary>
    /// The warehouse tasks for a storage interaction, or null when it is not a
    /// warehouse task slot. Rebuilds the index (throttled) on an unknown storage.
    /// </summary>
    private static List<ResourceStorageTaskData> TasksFor(StorageInteraction storage)
    {
        var key = storage.Pointer;
        if (_tasksByInteraction.TryGetValue(key, out var tasks)) return tasks;
        if (KnownUntasked.Contains(key)) return null;

        if (Time.realtimeSinceStartup - _lastRebuildAt >= RebuildThrottleSeconds)
        {
            RebuildIndex();
            if (_tasksByInteraction.TryGetValue(key, out tasks)) return tasks;
        }
        KnownUntasked.Add(key);
        return null;
    }

    /// <summary>
    /// The task for this item among a slot's tasks. A task with no item set
    /// applies to any item.
    /// </summary>
    private static ResourceStorageTaskData FindTask(List<ResourceStorageTaskData> tasks, ItemInfo info)
    {
        ResourceStorageTaskData anyItemTask = null;
        foreach (var task in tasks)
        {
            var taskInfo = task?.itemInfoQuantity?.itemInfo;
            if (taskInfo == null) { anyItemTask ??= task; continue; }
            if (taskInfo.id == info.id) return task;
        }
        return anyItemTask;
    }

    /// <summary>Logs a refusal once per villager, storage, item and reason, then returns false.</summary>
    private static bool Refuse(Villager villager, StorageInteraction storage, Item item, string reason)
    {
        var key = $"{villager?.Pointer}|{storage.Pointer}|{item.info?.id}|{reason}";
        if (LoggedRefusals.Add(key))
            DiagnosticLog.Write("storage_refused",
                $"{GameDescribe.Villager(villager)} storage={DiagnosticLog.Quote(storage.name)} " +
                $"structure={DiagnosticLog.Quote(StructureName(storage))} item={GameDescribe.Item(item)} reason={reason}");
        return false;
    }

    /// <summary>The name of the structure a storage belongs to, for the log.</summary>
    private static string StructureName(StorageInteraction storage)
    {
        try { return storage.GetComponentInParent<Structure>()?.name ?? "<none>"; }
        catch { return "<unreadable>"; }
    }
}

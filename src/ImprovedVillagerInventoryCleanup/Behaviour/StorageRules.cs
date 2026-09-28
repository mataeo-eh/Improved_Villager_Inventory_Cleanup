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
//   5. Building storage (0.6.1): a storage belonging to a workstation that is
//      not a warehouse task slot - a workshop's tool storage, a woodcutter's
//      stick pile - takes the item only if that workstation has a task for it
//      (then that task's priority and quantity apply, as above), or if the
//      station itself needs the item from this villager (IsItemNeededByVillager,
//      IsFuelNeededByVillager), which is what vanilla cleanup uses it for. The
//      0.6.0 test had a workshop villager fill the workshop's tool storage with
//      tools the workshop had no task for.
//
// Storages belonging to no workstation only need rule 1. When every storage
// refuses, the item goes to the last-resort drop point (Eye of Odin or outpost).
//
// The task index maps each warehouse slot interaction to its tasks. It is
// rebuilt from every ResourceStorage in the world on the scheduler's sweep, and
// on demand (at most every 2 s) when an unknown storage is checked. Task
// objects are read live, so a limit the player just changed applies at once.
//
// Depends on: DiagnosticLog, GameDescribe.

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
    /// Called by: Patches/StorageRulePatches.
    /// </summary>
    /// <param name="storage">The storage interaction the villager would use.</param>
    /// <param name="item">The item being put away.</param>
    /// <param name="villager">The villager cleaning up.</param>
    /// <param name="owner">
    /// The workstation the storage belongs to, or null when it belongs to none
    /// (or it is unknown). For the villager's own workstation storage this is
    /// their workstation; in the settlement search it is the site being searched.
    /// </param>
    internal static bool Allows(StorageInteraction storage, Item item, Villager villager, Workstation owner)
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

            // Rules 2-4: a warehouse slot, judged by its own tasks.
            var slotTasks = TasksFor(storage);
            if (slotTasks != null)
            {
                var slotTask = FindTask(slotTasks, info);
                if (slotTask == null) return Refuse(villager, storage, item, "no_task_for_item");
                return CheckTask(slotTask, container, info, count, villager, storage, item);
            }

            // Rule 5: any other storage of a workstation, judged by the station's tasks.
            if (owner == null) return true;
            var stationTask = FindStationTask(owner, info);
            if (stationTask != null)
                return CheckTask(stationTask, container, info, count, villager, storage, item);

            if (villager != null && (owner.IsItemNeededByVillager(info, villager) || owner.IsFuelNeededByVillager(info, villager)))
                return true;

            return Refuse(villager, storage, item,
                $"station_has_no_task_for_item station={DiagnosticLog.Quote(owner.name)}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("storage_rules_failed", exception);
            return false;
        }
    }

    /// <summary>
    /// Applies a task's priority (None refuses) and quantity (the stack must
    /// fit within it; 0 refuses) to this container. Called by: Allows.
    /// </summary>
    private static bool CheckTask(WorkstationTaskData task, ItemContainer container, ItemInfo info, int count,
        Villager villager, StorageInteraction storage, Item item)
    {
        var priority = task.priority;
        if (priority >= PriorityNone || priority < 0)
            return Refuse(villager, storage, item, $"task_priority_none raw_priority={priority}");

        var limit = task.itemInfoQuantity?.quantity ?? 0;
        var existing = container.GetItemCount(info);
        if (existing + count > limit)
            return Refuse(villager, storage, item, $"task_quantity existing={existing} adding={count} limit={limit}");

        return true;
    }

    /// <summary>
    /// The workstation's own task for this item (a crafting order, a gather
    /// quota...), or null. Only tasks that name this exact item count.
    /// Called by: Allows.
    /// </summary>
    private static WorkstationTaskData FindStationTask(Workstation owner, ItemInfo info)
    {
        var tasks = owner.GetWorkstationTaskDatas();
        if (tasks == null) return null;
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var taskInfo = task?.itemInfoQuantity?.itemInfo;
            if (taskInfo != null && taskInfo.id == info.id) return task;
        }
        return null;
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

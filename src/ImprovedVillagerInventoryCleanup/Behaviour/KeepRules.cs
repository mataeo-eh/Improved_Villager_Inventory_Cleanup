// KeepRules - items a villager keeps even though their job does not "need"
// them, so this mod never puts them away.
//
// Role in the larger system: CleanupBehaviourPatches asks ShouldKeep before it
// accepts an item this mod admitted as the cleanup's pick, and CleanupScheduler
// asks it when listing a villager's unneeded items. Two rules:
//
// 1. Own station's products (0.6.4). The 0.6.3 play test had craftsmen build
//    tools forever. In vanilla a crafter who cannot deposit a finished tool
//    (tool storage full) keeps holding it, and that is what stops the crafting.
//    The game's "needed at work" test does not cover a finished product, so
//    this mod cleaned it: no storage took it, it went to the Eye of Odin, the
//    crafter's hands were free again, and they crafted the next one. Now an
//    item the villager's own station has an active task to MAKE is kept, as in
//    vanilla.
//
//    A crafting task names a blueprint, not the tool: its itemInfo is a
//    BlueprintInfo whose "result" is the product (read from the interop
//    assemblies). Both the task's own item and a blueprint's result are
//    matched. Warehouse tasks (ResourceStorageTaskData) are storage tasks, not
//    production, and are ignored. A task with priority None is switched off
//    and does not count.
//
// 2. Temporary holds. When a last-resort drop has to be cancelled (the drop
//    point cannot be reached, or the game was about to drop the item on the
//    spot - see CleanupWatcher), the item is held for a few minutes so the
//    next scan does not pick the same item again straight away.
//
// Depends on: Plugin (config), DiagnosticLog, GameDescribe.

using System;
using System.Collections.Generic;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class KeepRules
{
    private const int PriorityNone = 3;   // WorkstationTaskPriority.None (High=0, Med=1, Low=2)

    // How long a cancelled last-resort drop keeps its item out of cleanup.
    private const float HoldSeconds = 300f;

    // "villager pointer|item pointer" -> realtime the hold ends.
    private static readonly Dictionary<string, float> HoldUntil = new();

    // Things already logged, so each is reported once: kept products
    // ("villager|item id") and stations whose task list was written out.
    private static readonly HashSet<string> LoggedProducts = new();
    private static readonly HashSet<IntPtr> LoggedStations = new();

    /// <summary>
    /// True when the villager keeps this item under either rule in the file
    /// header. Never throws; an unreadable item is not kept by these rules.
    /// Called by: CleanupCheckItemBehaviourPatch.IsAcceptable,
    /// CleanupScheduler.FindUnneeded.
    /// Calls: IsHeld, IsOwnStationProduct.
    /// </summary>
    /// <param name="villager">The villager cleaning up.</param>
    /// <param name="item">The inventory item being considered.</param>
    internal static bool ShouldKeep(Villager villager, Item item)
    {
        try
        {
            if (villager == null || item?.info == null) return false;
            if (IsHeld(villager, item)) return true;
            return Plugin.KeepOwnStationProducts.Value && IsOwnStationProduct(villager, item);
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("keep_rules_failed", exception);
            return false;
        }
    }

    /// <summary>
    /// Keeps this item out of cleanup for HoldSeconds.
    /// Called by: CleanupWatcher, when it cancels a last-resort drop.
    /// </summary>
    /// <param name="villager">The villager carrying the item.</param>
    /// <param name="item">The item whose drop was cancelled.</param>
    internal static void Hold(Villager villager, Item item)
    {
        if (villager == null || item == null) return;
        HoldUntil[HoldKey(villager, item)] = Time.realtimeSinceStartup + HoldSeconds;
    }

    /// <summary>True while a hold placed by Hold is still running. Expired holds are removed.</summary>
    private static bool IsHeld(Villager villager, Item item)
    {
        if (HoldUntil.Count == 0) return false;
        var key = HoldKey(villager, item);
        if (!HoldUntil.TryGetValue(key, out var until)) return false;
        if (Time.realtimeSinceStartup < until) return true;
        HoldUntil.Remove(key);
        return false;
    }

    private static string HoldKey(Villager villager, Item item) => $"{villager.Pointer}|{item.Pointer}";

    /// <summary>
    /// True when the villager's own workstation has an active task that
    /// produces this item (rule 1 in the file header). Logs each kept product
    /// once per villager.
    /// Calls: Produces, LogStationTasks.
    /// </summary>
    private static bool IsOwnStationProduct(Villager villager, Item item)
    {
        var workstation = villager.GetWorkstation();
        if (workstation == null) return false;

        var tasks = workstation.GetWorkstationTaskDatas();
        if (tasks == null) return false;
        LogStationTasks(workstation, tasks);

        var info = item.info;
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            if (task == null || !Produces(task, info)) continue;

            // A warehouse slot stores the item; it does not make it.
            if (task.TryCast<ResourceStorageTaskData>() != null) continue;

            var priority = task.priority;
            if (priority < 0 || priority >= PriorityNone) continue;

            if (LoggedProducts.Add($"{villager.Pointer}|{info.id}"))
                DiagnosticLog.Write("station_product_kept",
                    $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(item)} " +
                    $"station={DiagnosticLog.Quote(workstation.name)} task_priority={priority}");
            return true;
        }
        return false;
    }

    /// <summary>
    /// True when the task makes <paramref name="info"/>: the task names the
    /// item itself, or names a blueprint whose result is the item.
    /// </summary>
    private static bool Produces(WorkstationTaskData task, ItemInfo info)
    {
        var taskInfo = task.itemInfoQuantity?.itemInfo;
        if (taskInfo == null) return false;
        if (taskInfo.id == info.id) return true;

        var result = taskInfo.TryCast<BlueprintInfo>()?.result;
        return result != null && result.id == info.id;
    }

    /// <summary>
    /// Writes a station's task list to the diagnostic log the first time the
    /// station is checked, so a test log shows what each task names and makes.
    /// </summary>
    private static void LogStationTasks(Workstation workstation, Il2CppSystem.Collections.Generic.List<WorkstationTaskData> tasks)
    {
        if (!LoggedStations.Add(workstation.Pointer)) return;

        var descriptions = new List<string>();
        for (var index = 0; index < tasks.Count; index++)
        {
            var task = tasks[index];
            var taskInfo = task?.itemInfoQuantity?.itemInfo;
            if (taskInfo == null) continue;

            var result = taskInfo.TryCast<BlueprintInfo>()?.result;
            var makes = result == null ? "" : $" makes={result.Name}";
            descriptions.Add($"{taskInfo.Name}{makes} priority={task.priority} quantity={task.itemInfoQuantity.quantity}");
        }
        DiagnosticLog.Write("station_tasks",
            $"station={DiagnosticLog.Quote(workstation.name)} count={descriptions.Count} " +
            $"tasks={DiagnosticLog.Quote(string.Join("; ", descriptions))}");
    }
}

// StorageRulePatches - apply StorageRules to both ways the cleanup FSM picks
// a storage, so a cleaning villager never overfills a storage or ignores a
// warehouse task's quantity or priority.
//
// Role in the larger system (see Behaviour/StorageRules.cs for the rules):
//
//   Path 1 - the villager's own workstation storage. The FSM filters
//   candidates with CleanupInventoryData.StorageToDropIntoPredicate, so a
//   postfix there can refuse a storage directly.
//
//   Path 2 - Settlement.FindStorageToDeposit. The FSM's predicate
//   (ResourceStoragePredicate) only sees whole sites. The individual slots,
//   such as each tool rack in a warehouse, are judged inside by
//   Settlement._FindStorageInteractionToDepositPredicate, which also records
//   the best candidate as a side effect. So the rule is applied in a PREFIX
//   there, skipping the call entirely for a refused slot. That method is shared
//   with every hauler in the game, so it only acts during a cleanup search.
//   The cleanup search is recognised by the FSM's own ResourceStoragePredicate
//   running inside it (StorageSearchContext).
//
// Villagers away on a voyage, and the master switch being off, leave both
// paths vanilla.
//
// Depends on: HarmonyLib, Plugin (config), StorageRules, VoyageGuard.

using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Behaviour;
using SSSGame;
using SSSGame.AI.FSM;

namespace ImprovedVillagerInventoryCleanup.Patches;

/// <summary>
/// Which cleanup search, if any, Settlement.FindStorageToDeposit is running
/// right now. Main thread only.
/// </summary>
internal static class StorageSearchContext
{
    /// <summary>The cleanup data whose item is being placed, or null outside a cleanup search.</summary>
    internal static FSM_CleanupInventory.CleanupInventoryData Data;

    /// <summary>
    /// True when StorageRules should be applied for this cleanup data: the
    /// mod is on and the villager is not away on a voyage.
    /// </summary>
    internal static bool Applies(FSM_CleanupInventory.CleanupInventoryData data) =>
        data != null && Plugin.EnableImprovedCleanup.Value && !VoyageGuard.IsExempt(data.agent);
}

/// <summary>Path 1: refuse the villager's own workstation storage when it breaks the rules.</summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.StorageToDropIntoPredicate))]
internal static class WorkstationStorageRulePatch
{
    /// <summary>
    /// Turns a vanilla "yes" into "no" when StorageRules refuses. Runs before
    /// the diagnostic postfix so the log shows the final answer.
    /// Calls: StorageRules.Allows.
    /// </summary>
    [HarmonyPriority(Priority.High)]
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, StorageInteraction __0, ref bool __result)
    {
        if (!__result || !StorageSearchContext.Applies(__instance)) return;
        if (!StorageRules.Allows(__0, __instance.itemToDrop, __instance.agent)) __result = false;
    }
}

/// <summary>Path 2, start: a settlement storage search begins with no cleanup context.</summary>
[HarmonyPatch(typeof(Settlement), nameof(Settlement.FindStorageToDeposit))]
internal static class StorageSearchScopePatch
{
    private static void Prefix() => StorageSearchContext.Data = null;
    private static void Postfix() => StorageSearchContext.Data = null;
}

/// <summary>
/// The only other caller of the slot predicate (read from the binary). Reset
/// the context around it too, so a trashcan search never inherits a cleanup's
/// rules.
/// </summary>
[HarmonyPatch(typeof(Settlement), nameof(Settlement.FindTrashcanToDeposit))]
internal static class TrashcanSearchScopePatch
{
    private static void Prefix() => StorageSearchContext.Data = null;
    private static void Postfix() => StorageSearchContext.Data = null;
}

/// <summary>
/// Path 2, marker: the cleanup FSM's site predicate runs inside
/// FindStorageToDeposit, which tells us this search is a cleanup search.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.ResourceStoragePredicate))]
internal static class StorageSearchMarkerPatch
{
    private static void Prefix(FSM_CleanupInventory.CleanupInventoryData __instance) =>
        StorageSearchContext.Data = __instance;
}

/// <summary>Path 2, the rule: skip a warehouse slot that breaks the rules.</summary>
[HarmonyPatch(typeof(Settlement), nameof(Settlement._FindStorageInteractionToDepositPredicate))]
internal static class SettlementSlotRulePatch
{
    /// <summary>
    /// During a cleanup search, refuses a slot StorageRules rejects, without
    /// running the original (which would record it as the best candidate).
    /// Outside a cleanup search it does nothing.
    /// Calls: StorageRules.Allows.
    /// </summary>
    /// <returns>False to skip the original for a refused slot.</returns>
    private static bool Prefix(StorageInteraction __0, ref bool __result)
    {
        var data = StorageSearchContext.Data;
        if (!StorageSearchContext.Applies(data)) return true;
        if (StorageRules.Allows(__0, data.itemToDrop, data.agent)) return true;

        __result = false;
        return false;
    }
}

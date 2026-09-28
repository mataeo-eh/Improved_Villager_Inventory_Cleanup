// StorageRulePatches - apply StorageRules to both ways the cleanup FSM picks
// a storage, so a cleaning villager never overfills a storage or ignores a
// warehouse's or workstation's task quantity or priority.
//
// Role in the larger system (see Behaviour/StorageRules.cs for the rules):
//
//   Path 1 - the villager's own workstation storage. The FSM filters
//   candidates with CleanupInventoryData.StorageToDropIntoPredicate, so a
//   postfix there refuses a storage directly. The storage's owner is the
//   villager's workstation.
//
//   Path 2 - Settlement.FindStorageToDeposit. The FSM's predicate
//   (ResourceStoragePredicate) sees each site (warehouse, station) in turn.
//   The site's individual slots are judged inside by
//   Settlement._FindStorageInteractionToDepositPredicate, which also records
//   the best candidate as a side effect. So the rule is applied in a PREFIX
//   there, skipping the call for a refused slot. The owner is the site that
//   ResourceStoragePredicate last saw.
//
// Recognising a cleanup search in path 2 is the delicate part.
// _FindStorageInteractionToDepositPredicate is shared with every hauler (and
// the trashcan search). 0.6.0 marked the search by patching
// FindStorageToDeposit and FindTrashcanToDeposit themselves. Il2CppInterop
// cannot marshal their by-reference Vector3&/Single& parameters, so every call
// threw (128 NullReferenceExceptions in the 0.6.0 test log) and the storage
// search failed for EVERY villager, haulers included. Never patch those two
// methods.
//
// Instead a slot is judged by cleanup rules only when all of these hold:
//   - the cleanup FSM's ResourceStoragePredicate ran in this same frame
//     (FindStorageToDeposit calls it for each site before judging that site's
//     slots);
//   - the settlement's current search item (Settlement._itemToDeposit, set
//     by FindStorageToDeposit) is the cleanup's item;
//   - the search is not the "ignore limits" variant (_ignoreSurplusLimits),
//     which the cleanup only uses to pick a spot to drop beside. The mod
//     redirects that drop to the Eye of Odin anyway.
//
// Villagers away on a voyage, and the master switch being off, leave both
// paths vanilla.
//
// Depends on: HarmonyLib, Plugin (config), StorageRules, VoyageGuard.

using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Behaviour;
using SSSGame;
using SSSGame.AI.FSM;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Patches;

/// <summary>The cleanup search, if any, the settlement is running. Main thread only.</summary>
internal static class StorageSearchContext
{
    /// <summary>The cleanup data whose item is being placed.</summary>
    internal static FSM_CleanupInventory.CleanupInventoryData Data;

    /// <summary>The site ResourceStoragePredicate last saw, as a workstation (null if not one).</summary>
    internal static Workstation Site;

    /// <summary>Time.frameCount when Data and Site were set.</summary>
    internal static int Frame = -1;

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
        var villager = __instance.agent;
        if (!StorageRules.Allows(__0, __instance.itemToDrop, villager, villager?.GetWorkstation()))
            __result = false;
    }
}

/// <summary>
/// Path 2, marker: the cleanup FSM's site predicate runs inside
/// FindStorageToDeposit once per site, just before that site's slots are judged.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.ResourceStoragePredicate))]
internal static class StorageSearchMarkerPatch
{
    private static void Prefix(FSM_CleanupInventory.CleanupInventoryData __instance, IResourceStorageSite __0)
    {
        StorageSearchContext.Data = __instance;
        StorageSearchContext.Site = __0?.TryCast<Workstation>();
        StorageSearchContext.Frame = Time.frameCount;
    }
}

/// <summary>Path 2, the rule: skip a slot that breaks the rules.</summary>
[HarmonyPatch(typeof(Settlement), nameof(Settlement._FindStorageInteractionToDepositPredicate))]
internal static class SettlementSlotRulePatch
{
    /// <summary>
    /// During a cleanup search (see the file header), refuses a slot
    /// StorageRules rejects, without running the original (which would record
    /// it as the best candidate). Any other search is left alone.
    /// Calls: StorageRules.Allows.
    /// </summary>
    /// <returns>False to skip the original for a refused slot.</returns>
    private static bool Prefix(Settlement __instance, StorageInteraction __0, ref bool __result)
    {
        var data = StorageSearchContext.Data;
        if (StorageSearchContext.Frame != Time.frameCount || !StorageSearchContext.Applies(data)) return true;

        var item = data.itemToDrop;
        var searchItem = __instance._itemToDeposit;
        if (item?.info == null || searchItem == null || searchItem.id != item.info.id) return true;
        if (__instance._ignoreSurplusLimits) return true;

        if (StorageRules.Allows(__0, item, data.agent, StorageSearchContext.Site)) return true;
        __result = false;
        return false;
    }
}

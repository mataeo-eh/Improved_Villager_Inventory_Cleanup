// EyeOfOdinPatches - the debug "force last-resort drops" mode.
//
// Role in the larger system
// -------------------------
// Normal play: villagers deposit into storage, and only drop an item at the
// Eye of Odin (or their outpost) when no storage will take it. That redirect
// lives in CleanupWatcher and needs no patch.
//
// Debug.ForceLastResortDrops: skip storage entirely, so every cleaned-up item
// goes to the drop point. Used in 0.4.0 to prove cleanup works regardless of
// storage space.
//
// How the cleanup FSM chooses a destination (read from the game binary with
// scripts/disasm.ps1, FSM_CleanupInventory.OnStateUpdate):
//
//   1. If the villager's workstation is itself a storage site, ask it for a
//      slot, filtering with CleanupInventoryData.StorageToDropIntoPredicate.
//   2. Otherwise Settlement.FindStorageToDeposit, filtering with
//      CleanupInventoryData.ResourceStoragePredicate.
//   3. If both fail: walk to a fallback spot and drop the item there, which
//      CleanupWatcher redirects to the drop point.
//
// Force mode makes both predicates refuse, so every pick reaches step 3.
//
// Depends on: HarmonyLib, Plugin (config), VoyageGuard.

using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Behaviour;
using SSSGame.AI.FSM;

namespace ImprovedVillagerInventoryCleanup.Patches;

/// <summary>Decides whether force mode applies to a villager's cleanup.</summary>
internal static class ForceEyeOfOdin
{
    /// <summary>
    /// True when force mode is on, and the villager has a workstation (vanilla's
    /// step 3 has nowhere to walk a villager without one) and is not away on a
    /// voyage. Never throws.
    /// Called by: the two storage predicate postfixes below.
    /// </summary>
    /// <param name="data">The villager's cleanup FSM state.</param>
    internal static bool AppliesTo(FSM_CleanupInventory.CleanupInventoryData data)
    {
        if (!Plugin.ForceEyeOfOdinDrops.Value || data == null) return false;
        try
        {
            var villager = data.agent;
            return villager != null && villager.GetWorkstation() != null && !VoyageGuard.IsExempt(villager);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Force mode, step 1: the villager's own workstation storage refuses.</summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.StorageToDropIntoPredicate))]
internal static class ForceStorageToDropIntoPatch
{
    [HarmonyPriority(Priority.High)]
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, ref bool __result)
    {
        if (__result && ForceEyeOfOdin.AppliesTo(__instance)) __result = false;
    }
}

/// <summary>Force mode, step 2: settlement storages refuse.</summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.ResourceStoragePredicate))]
internal static class ForceResourceStoragePatch
{
    [HarmonyPriority(Priority.High)]
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, ref bool __result)
    {
        if (__result && ForceEyeOfOdin.AppliesTo(__instance)) __result = false;
    }
}

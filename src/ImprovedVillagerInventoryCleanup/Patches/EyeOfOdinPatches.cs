// EyeOfOdinPatches - sends villagers to drop cleaned-up items on the ground in
// front of the Eye of Odin instead of into storage.
//
// Role in the larger system
// -------------------------
// This is a test mode. It exists to prove that cleanup itself works,
// independently of whether any storage has room: if items pile up at the Eye,
// the scan picked them and the villager acted on them.
//
// How the cleanup FSM chooses a destination (read from the game binary with
// scripts/disasm.ps1, FSM_CleanupInventory.OnStateUpdate):
//
//   1. If the villager's workstation is itself a storage site, ask it for a
//      slot, filtering with CleanupInventoryData.StorageToDropIntoPredicate.
//      In the 0.3.0 test this is where every deposit happened, which is why
//      0.3.0's force mode (which only blocked step 2) never took effect.
//   2. Otherwise Settlement.FindStorageToDeposit, twice, filtering with
//      CleanupInventoryData.ResourceStoragePredicate.
//   3. If both fail: walk to a fallback transform (the quest's workstation)
//      and drop the item on the ground there.
//
// Force mode makes both predicates refuse every storage, so every pick goes to
// step 3. CleanupWatcher then swaps step 3's walk target for a point in front
// of the Eye of Odin.
//
// Known gap: for some Buildstation (builder) quests, vanilla's step 3 drops the
// item where the villager stands instead of walking. The drop still happens,
// just not at the Eye.
//
// The Eye of Odin is taken to be the settlement core (Settlement._settlementCore),
// the structure that founds a settlement. Its name is logged when first found
// so a test run confirms that.
//
// Depends on: HarmonyLib, Plugin (config), DiagnosticLog, UnityEngine.

using System;
using System.Collections.Generic;
using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame;
using SSSGame.AI.FSM;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Patches;

/// <summary>
/// Finds the Eye of Odin for a villager's settlement and keeps a marker object
/// standing in front of it, which the cleanup FSM walks villagers to.
/// </summary>
internal static class EyeOfOdinDropPoint
{
    // settlement pointer -> marker. One marker per settlement, so villagers of
    // different settlements never pull each other's walk target around.
    private static readonly Dictionary<IntPtr, GameObject> MarkerBySettlement = new();

    // Settlements whose core has already been logged, to log each once.
    private static readonly HashSet<IntPtr> LoggedSettlements = new();

    /// <summary>
    /// Returns a transform standing EyeOfOdinStandOffDistance metres in front of
    /// the villager's settlement core, creating or moving the marker as needed.
    /// Returns null when the villager has no settlement or it has no core.
    /// Called by: Behaviour.CleanupWatcher.TryRedirect.
    /// </summary>
    /// <param name="villager">The villager about to walk there.</param>
    /// <returns>The marker's transform, or null if there is no Eye of Odin.</returns>
    internal static Transform Get(Villager villager)
    {
        var settlement = villager?.GetSettlement();
        if (settlement == null) return null;

        var core = settlement._settlementCore;
        if (core == null) return null;

        var coreTransform = core.transform;
        var standPoint = coreTransform.position + coreTransform.forward * Plugin.EyeOfOdinStandOffDistance.Value;

        var key = settlement.Pointer;
        if (!MarkerBySettlement.TryGetValue(key, out var marker) || marker == null)
        {
            marker = new GameObject("ImprovedVillagerInventoryCleanup.EyeOfOdinDropPoint");
            UnityEngine.Object.DontDestroyOnLoad(marker);
            marker.hideFlags = HideFlags.HideAndDontSave;
            MarkerBySettlement[key] = marker;
        }
        marker.transform.position = standPoint;

        if (LoggedSettlements.Add(key))
            DiagnosticLog.Write("eye_of_odin_resolved",
                $"core_name={DiagnosticLog.Quote(core.name)} core_position={coreTransform.position} " +
                $"stand_point={standPoint} stand_off={Plugin.EyeOfOdinStandOffDistance.Value:0.##}");

        return marker.transform;
    }
}


/// <summary>
/// Decides whether force mode applies to a villager. Villagers without a
/// workstation are left on vanilla behaviour, because vanilla's step 3 has
/// nowhere to walk them.
/// </summary>
internal static class ForceEyeOfOdin
{
    /// <summary>
    /// True when force mode is on and this villager can use the ground-drop
    /// path. Never throws.
    /// Called by: the two storage predicate postfixes below.
    /// </summary>
    /// <param name="data">The villager's cleanup FSM state.</param>
    internal static bool AppliesTo(FSM_CleanupInventory.CleanupInventoryData data)
    {
        if (!Plugin.ForceEyeOfOdinDrops.Value || data == null) return false;
        try
        {
            return data.agent?.GetWorkstation() != null;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Force mode, step 1: the villager's own workstation storage refuses.
/// Runs before the diagnostic postfix, so the log shows the forced answer.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.StorageToDropIntoPredicate))]
internal static class ForceStorageToDropIntoPatch
{
    [HarmonyPriority(Priority.High)]
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, ref bool __result)
    {
        if (__result && ForceEyeOfOdin.AppliesTo(__instance)) __result = false;
    }
}

/// <summary>
/// Force mode, step 2: settlement storages refuse.
/// Runs before the diagnostic postfix, so the log shows the forced answer.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.ResourceStoragePredicate))]
internal static class ForceResourceStoragePatch
{
    [HarmonyPriority(Priority.High)]
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, ref bool __result)
    {
        if (__result && ForceEyeOfOdin.AppliesTo(__instance)) __result = false;
    }
}

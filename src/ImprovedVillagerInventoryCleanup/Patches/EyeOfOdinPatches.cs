// EyeOfOdinPatches - sends villagers to drop cleaned-up items on the ground in
// front of the Eye of Odin instead of into storage.
//
// Role in the larger system
// -------------------------
// This is a test fallback. It exists to prove that cleanup itself works,
// independently of whether any storage has room: if items pile up at the Eye,
// the scan picked them and the villager acted on it.
//
// How it rides on vanilla (read from the game binary with scripts/disasm.ps1):
// after the scan picks an item, FSM_CleanupInventory.OnStateUpdate calls
// Settlement.FindStorageToDeposit up to twice. If both come back empty, it sets
// CleanupInventoryData.workstationTransform to the villager's workstation, then
// on later ticks walks the villager to that transform and drops the item on
// the ground once within minimumDistance. We reuse that whole path and only:
//
//   1. make FindStorageToDeposit return nothing during a cleanup update
//      (ForceEyeOfOdinDrops), so every item goes down the ground-drop path;
//   2. swap workstationTransform for a marker placed in front of the Eye of
//      Odin, in the same update that vanilla set it, whenever no storage was
//      found (whether forced by 1, or because storage really had no room).
//
// Known gaps, all logged so a test run shows them:
//   - vanilla throws when a villager with no workstation finds no storage, so
//     forcing is skipped for villagers without a workstation;
//   - for some Buildstation villagers vanilla drops the item where they stand
//     instead of walking; the swapped transform is then unused.
//
// The Eye of Odin is taken to be the settlement core (Settlement._settlementCore),
// the structure that founds a settlement. Its name is logged when first found
// so a test run confirms that.
//
// Depends on: HarmonyLib, Plugin (config), CleanupScanContext, DiagnosticLog,
// GameDescribe, UnityEngine.

using System;
using System.Collections.Generic;
using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Behaviour;
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
    /// Called by: CleanupOnStateUpdatePatch.Postfix.
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
/// Opens and closes the per-update context, and after the update redirects the
/// villager's ground-drop walk to the Eye of Odin when no storage was found.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory), nameof(FSM_CleanupInventory.OnStateUpdate))]
internal static class CleanupOnStateUpdatePatch
{
    private static void Prefix() => CleanupScanContext.BeginUpdate();

    /// <summary>
    /// If this update scanned the inventory, picked an item, searched for
    /// storage and found none, then vanilla has just pointed
    /// workstationTransform at the villager's workstation. Point it at the Eye
    /// of Odin instead.
    /// Calls: EyeOfOdinDropPoint.Get, CleanupScanContext.EndUpdate.
    /// </summary>
    private static void Postfix()
    {
        var data = CleanupScanContext.ScannedData;
        var searches = CleanupScanContext.StorageSearches;
        var storageFound = CleanupScanContext.StorageFound;
        CleanupScanContext.EndUpdate();

        if (data == null || searches == 0 || storageFound) return;
        if (!Plugin.EyeOfOdinFallback.Value && !Plugin.ForceEyeOfOdinDrops.Value) return;

        try
        {
            var item = data.itemToDrop;
            var previousTarget = data.workstationTransform;
            if (item == null || previousTarget == null) return;

            var villager = data.agent;
            var marker = EyeOfOdinDropPoint.Get(villager);
            if (marker == null)
            {
                DiagnosticLog.Write("eye_of_odin_unavailable",
                    $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(item)}");
                return;
            }
            if (previousTarget.Pointer == marker.Pointer) return;

            data.workstationTransform = marker;
            DiagnosticLog.Write("eye_of_odin_redirect",
                $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(item)} " +
                $"forced={Plugin.ForceEyeOfOdinDrops.Value} replaced_target={DiagnosticLog.Quote(previousTarget.name)}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("eye_of_odin_redirect_failed", exception);
        }
    }
}

/// <summary>
/// Hides every storage from the cleanup FSM when ForceEyeOfOdinDrops is on, and
/// records whether a storage was found either way.
/// </summary>
/// <remarks>
/// FindStorageToDeposit is also used by hauling and other jobs. The patch acts
/// only between the OnStateUpdate prefix and postfix of the cleanup FSM, after
/// that update has scanned an inventory.
/// </remarks>
[HarmonyPatch(typeof(Settlement), nameof(Settlement.FindStorageToDeposit))]
internal static class CleanupFindStoragePatch
{
    /// <summary>
    /// Skips the search (returning no storage) while forcing is on. Villagers
    /// with no workstation are left alone, because vanilla throws when such a
    /// villager finds no storage.
    /// </summary>
    /// <param name="__result">Set to null when the search is skipped.</param>
    /// <returns>False to skip the vanilla search, true to run it.</returns>
    private static bool Prefix(ref Interaction __result)
    {
        var data = CleanupScanContext.ScannedData;
        if (!CleanupScanContext.InUpdate || data == null) return true;
        if (!Plugin.ForceEyeOfOdinDrops.Value) return true;

        try
        {
            if (data.agent?.GetWorkstation() == null) return true;
        }
        catch
        {
            return true;
        }

        __result = null;
        return false;
    }

    /// <summary>Records the outcome of a cleanup storage search.</summary>
    /// <param name="__result">The storage found, or null.</param>
    private static void Postfix(Interaction __result)
    {
        if (!CleanupScanContext.InUpdate || CleanupScanContext.ScannedData == null) return;
        CleanupScanContext.NoteStorageSearch(__result != null);
    }
}

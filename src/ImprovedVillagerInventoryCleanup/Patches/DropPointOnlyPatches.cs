// DropPointOnlyPatches - makes a last-resort drop happen at the drop point (the
// Eye of Odin or the villager's outpost) and nowhere else.
//
// Role in the larger system: wraps every tick of the cleanup FSM,
// FSM_CleanupInventory.OnStateUpdate, and lets CleanupWatcher act at the two
// moments that matter. Behaviour/CleanupWatcher.cs ("Only at the drop point")
// explains why this is needed; in short, the game decides a villager has
// "arrived" from a stale number, and so drops items wherever the villager
// happened to be standing.
//
//   Prefix   asks CleanupWatcher.BeforeFsmTick how "arrived" must be judged for
//            this villager on this tick, and writes that into the FSM's
//            minimumDistance. That field belongs to the FSM asset, which every
//            villager using it shares, so it is written before EVERY tick: the
//            watcher's answer when it has one, the authored value otherwise.
//            One villager's value therefore never leaks into another's tick.
//   Postfix  lets CleanupWatcher.AfterFsmTick cancel a drop on the spot.
//
// minimumDistance is read in exactly one place, the ground-drop walk (checked
// with scripts/disasm.ps1), so nothing else in the FSM notices the change.
// OnStateUpdate takes one interface argument and no by-reference parameters,
// so it is safe to patch (unlike Settlement.FindStorageToDeposit).
//
// Depends on: HarmonyLib, Plugin (config), CleanupWatcher, DiagnosticLog.

using System;
using System.Collections.Generic;
using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Behaviour;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;

namespace ImprovedVillagerInventoryCleanup.Patches;

[HarmonyPatch(typeof(FSM_CleanupInventory), nameof(FSM_CleanupInventory.OnStateUpdate))]
internal static class DropPointOnlyPatch
{
    // FSM asset pointer -> the minimumDistance it was authored with, read the
    // first time the asset is seen, before this patch ever writes to it.
    private static readonly Dictionary<IntPtr, float> AuthoredDistanceByFsm = new();

    // Failures are logged a few times, not on every tick of every villager.
    private const int MaxLoggedFailures = 5;
    private static int _loggedFailures;

    /// <summary>
    /// Sets the arrival distance for this tick (see the file header).
    /// Never throws: on any failure the authored distance is used, which is
    /// vanilla behaviour.
    /// Calls: VillagerOf, CleanupWatcher.BeforeFsmTick.
    /// </summary>
    /// <param name="__instance">The cleanup FSM asset.</param>
    /// <param name="__0">The FSM controller of the villager being ticked.</param>
    private static void Prefix(FSM_CleanupInventory __instance, IFSMBehaviourController __0)
    {
        if (__instance == null) return;

        var authored = 0f;
        var authoredKnown = false;
        try
        {
            var key = __instance.Pointer;
            if (!AuthoredDistanceByFsm.TryGetValue(key, out authored))
            {
                authored = __instance.minimumDistance;
                AuthoredDistanceByFsm[key] = authored;
                DiagnosticLog.Write("drop_point_only_active",
                    $"fsm={DiagnosticLog.Quote(__instance.name)} authored_minimum_distance={authored:0.##} " +
                    $"drop_radius={Plugin.DropRadius.Value:0.##}");
            }
            authoredKnown = true;

            float? wanted = null;
            if (Plugin.EnableImprovedCleanup.Value)
                wanted = CleanupWatcher.BeforeFsmTick(VillagerOf(__instance, __0));
            else
                CleanupWatcher.BeforeFsmTick(null);   // only clears the per-tick hand-off

            __instance.minimumDistance = wanted ?? authored;
        }
        catch (Exception exception)
        {
            if (authoredKnown) __instance.minimumDistance = authored;
            if (_loggedFailures++ < MaxLoggedFailures)
                DiagnosticLog.WriteException("drop_point_only_prefix_failed", exception);
        }
    }

    /// <summary>
    /// Lets the watcher cancel a drop the tick decided to make on the spot.
    /// Never throws. Calls: CleanupWatcher.AfterFsmTick.
    /// </summary>
    private static void Postfix()
    {
        try
        {
            CleanupWatcher.AfterFsmTick();
        }
        catch (Exception exception)
        {
            if (_loggedFailures++ < MaxLoggedFailures)
                DiagnosticLog.WriteException("drop_point_only_postfix_failed", exception);
        }
    }

    /// <summary>
    /// The villager a tick belongs to, found the way the FSM itself does it:
    /// the controller's cleanup quest data knows its villager.
    /// </summary>
    /// <param name="fsm">The cleanup FSM asset.</param>
    /// <param name="controller">The FSM controller being ticked.</param>
    /// <returns>The villager, or null when this is not a villager's cleanup quest.</returns>
    private static Villager VillagerOf(FSM_CleanupInventory fsm, IFSMBehaviourController controller)
    {
        if (controller == null) return null;
        var questData = fsm.GetQuestData(controller);
        var cleanupData = questData?.TryCast<CleanupInventoryQuest.CleanupInventoryQuestData>();
        return cleanupData?.GetVillager();
    }
}

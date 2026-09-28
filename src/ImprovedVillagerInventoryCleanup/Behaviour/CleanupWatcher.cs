// CleanupWatcher - follows every villager's cleanup FSM state once per frame,
// logs each decision it makes, and applies the last-resort drop redirect.
//
// Role in the larger system
// -------------------------
// The cleanup FSM keeps its whole working state on a CleanupInventoryData
// object: the item it picked (itemToDrop), where it is depositing it
// (interaction - a storage), and, when it found no storage, the place it is
// walking to before dropping the item on the ground (workstationTransform).
// Reading that object every frame tells us exactly what each villager decided,
// without hooking the FSM's own update. (0.3.0 did hook it, and the 0.3.0 test
// log could not show whether that hook ever ran.)
//
// Three outcomes, as read from FSM_CleanupInventory.OnStateUpdate:
//   deposit      itemToDrop set, interaction = a storage   -> walks there, stores it
//   ground drop  itemToDrop set, no interaction, target set -> walks to target, drops it
//   nothing      itemToDrop null                           -> nothing left to clean
//
// The redirect: when a villager is on the ground-drop path (no storage anywhere
// took the item), the target is swapped for a point in front of the Eye of Odin,
// or in front of their outpost if they live at one (DropPoint). Villagers away on
// a karvi voyage are left alone (VoyageGuard).
// Vanilla re-reads the target every tick while walking, so the swap takes
// effect on the villager's next step.
//
// Data objects are registered by the CheckItem prefix, i.e. whenever a
// villager scans their inventory.
//
// Depends on: Plugin (config), DropPoint, VoyageGuard, DiagnosticLog,
// DiagnosticTracker, GameDescribe.

using System;
using System.Collections.Generic;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame.AI.FSM;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class CleanupWatcher
{
    /// <summary>One watched cleanup data object and the last state logged for it.</summary>
    private sealed class Watched
    {
        internal FSM_CleanupInventory.CleanupInventoryData Data;
        internal string LastState = "";

        // When LastState began, and whether a stall has been reported for it.
        internal float StateSince;
        internal bool StallReported;
    }

    // A villager holding the same pick, going to the same place, for this long
    // is treated as stuck. The longest healthy run in the 0.3.0 log moved to its
    // next item within about 10 seconds.
    private const float StallSeconds = 30f;

    // data pointer -> watched entry. A villager's FSM reuses one data object,
    // so this stays roughly one entry per villager.
    private static readonly Dictionary<IntPtr, Watched> ByPointer = new();

    /// <summary>
    /// Starts watching a cleanup data object. Cheap when already watched, which
    /// matters because it is called for every item of every scan.
    /// Called by: CleanupCheckItemBehaviourPatch.Prefix.
    /// </summary>
    /// <param name="data">The villager's cleanup FSM state.</param>
    internal static void Register(FSM_CleanupInventory.CleanupInventoryData data)
    {
        if (data == null) return;
        var key = data.Pointer;
        if (key == IntPtr.Zero || ByPointer.ContainsKey(key)) return;
        ByPointer[key] = new Watched { Data = data };
    }

    /// <summary>
    /// Checks every watched villager: logs any change of decision, and
    /// redirects ground drops to the Eye of Odin when enabled. Never throws.
    /// Called by: DiagnosticBehaviour.Update, once per frame.
    /// </summary>
    internal static void Tick()
    {
        if (ByPointer.Count == 0) return;

        List<IntPtr> dead = null;
        foreach (var pair in ByPointer)
        {
            try
            {
                Inspect(pair.Value);
            }
            catch (Exception exception)
            {
                // A destroyed villager leaves a dead object behind; stop watching it.
                dead ??= new List<IntPtr>();
                dead.Add(pair.Key);
                DiagnosticLog.Write("cleanup_watch_dropped", $"reason={DiagnosticLog.Quote(exception.Message)}");
            }
        }

        if (dead == null) return;
        foreach (var key in dead) ByPointer.Remove(key);
    }

    /// <summary>
    /// Reads one villager's cleanup state, applies the redirect if it is on the
    /// ground-drop path, and logs the state when it differs from last frame.
    /// Called by: Tick.
    /// </summary>
    /// <param name="watched">The entry to inspect.</param>
    private static void Inspect(Watched watched)
    {
        var data = watched.Data;
        var item = data.itemToDrop;
        var storage = data.interaction;
        var target = data.workstationTransform;
        var villager = data.agent;

        string outcome;
        if (item == null) outcome = "nothing";
        else if (storage != null) outcome = "deposit";
        else if (target != null) outcome = "ground_drop";
        else outcome = "picked";   // item chosen, destination not decided yet

        var redirected = false;
        if (outcome == "ground_drop" && EyeOfOdinEnabled() && !VoyageGuard.IsExempt(villager))
            redirected = TryRedirect(data, villager, target);

        // Build the state from the values after any redirect, so a redirect is
        // logged once, not every frame.
        target = data.workstationTransform;
        var state = $"{outcome}|{item?.Pointer}|{storage?.Pointer}|{target?.Pointer}";
        var now = UnityEngine.Time.realtimeSinceStartup;
        if (state == watched.LastState)
        {
            // Unchanged. Report once if a villager has been stuck on one item
            // for too long - e.g. walking to a spot they can never reach.
            if (item == null || watched.StallReported || now - watched.StateSince < StallSeconds) return;
            watched.StallReported = true;
            DiagnosticLog.Write("cleanup_stalled",
                $"{GameDescribe.Villager(villager)} outcome={outcome} seconds={now - watched.StateSince:0} " +
                $"item={GameDescribe.Item(item)} storage={DescribeObject(storage)} walk_target={DescribeObject(target)} " +
                $"walk_target_position={(target == null ? "<none>" : target.position.ToString())} " +
                $"villager_position={SafePosition(villager)}");
            return;
        }
        watched.LastState = state;
        watched.StateSince = now;
        watched.StallReported = false;

        if (item == null && !DiagnosticTracker.IsTracked(villager)) return;
        DiagnosticLog.Write("cleanup_state",
            $"{GameDescribe.Villager(villager)} outcome={outcome} item={GameDescribe.Item(item)} " +
            $"priority={data.itemToDropPriority} storage={DescribeObject(storage)} " +
            $"walk_target={DescribeObject(target)} redirected_to_drop_point={redirected}");
    }

    /// <summary>True when either Eye of Odin setting asks for ground drops to go there.</summary>
    private static bool EyeOfOdinEnabled() =>
        Plugin.EyeOfOdinFallback.Value || Plugin.ForceEyeOfOdinDrops.Value;

    /// <summary>
    /// Points the villager's ground-drop walk at the Eye of Odin marker.
    /// Returns true only on the frame the swap happens.
    /// Called by: Inspect.
    /// </summary>
    /// <param name="data">The villager's cleanup FSM state.</param>
    /// <param name="villager">The villager doing the cleanup.</param>
    /// <param name="currentTarget">Where vanilla is sending them now.</param>
    /// <returns>True if the target was changed.</returns>
    private static bool TryRedirect(FSM_CleanupInventory.CleanupInventoryData data, SSSGame.Villager villager, UnityEngine.Transform currentTarget)
    {
        var marker = DropPoint.Get(villager);
        if (marker == null)
        {
            DiagnosticLog.Write("drop_point_unavailable",
                $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(data.itemToDrop)}");
            return false;
        }
        if (currentTarget.Pointer == marker.Pointer) return false;

        data.workstationTransform = marker;
        DiagnosticLog.Write("drop_point_redirect",
            $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(data.itemToDrop)} " +
            $"forced={Plugin.ForceEyeOfOdinDrops.Value} replaced_target={DiagnosticLog.Quote(currentTarget.name)}");
        return true;
    }

    /// <summary>The villager's world position for the log, or a placeholder.</summary>
    private static string SafePosition(SSSGame.Villager villager)
    {
        try { return villager == null ? "<none>" : villager.transform.position.ToString(); }
        catch { return "<unreadable>"; }
    }

    /// <summary>Names a Unity object for the log, or "&lt;none&gt;".</summary>
    private static string DescribeObject(UnityEngine.Object value)
    {
        if (value == null) return "<none>";
        try { return DiagnosticLog.Quote(value.name); }
        catch { return "<unreadable>"; }
    }
}

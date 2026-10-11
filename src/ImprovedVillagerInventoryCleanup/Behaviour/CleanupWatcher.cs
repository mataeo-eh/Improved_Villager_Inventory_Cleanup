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
// Only at the drop point (0.6.4)
// ------------------------------
// The 0.6.3 play test had tools dropped at eating places and workstations.
// Two causes, both read from FSM_CleanupInventory.OnStateUpdate:
//
//   1. "Arrived" is not measured to the walk target. While walking to a ground
//      drop the game drops the item as soon as
//          aiController.RemainingDistance <= fsm.minimumDistance
//      and it checks that BEFORE giving the walk order. RemainingDistance is
//      whatever was left of the villager's previous walk, so a villager who is
//      standing still (eating, at their station) has "arrived" on the first
//      tick and drops the item where they stand.
//   2. A settlement builder with no storage to walk to skips the walk and
//      drops the item on the spot.
//
// The fix runs around every cleanup FSM tick (Patches/DropPointOnlyPatches):
//
//   BeforeFsmTick  redirects a ground drop to the drop point straight away,
//                  then tells the patch which minimumDistance this tick must
//                  use: one no walk can satisfy while the villager is further
//                  than LastResort.DropRadius from the drop point, and one
//                  every walk satisfies once they are inside it. So the true
//                  distance to the drop point decides, not the stale number.
//   AfterFsmTick   spots a tick that picked an item but chose neither a
//                  storage nor a walk target (cause 2) and cancels that drop.
//
// A villager who stops moving for StillSeconds on the way is either dropped
// from where they stand (close enough to count as "at the drop point") or has
// the drop cancelled and keeps the item (KeepRules.Hold).
//
// Depends on: Plugin (config), DropPoint, KeepRules, VoyageGuard,
// DiagnosticLog, DiagnosticTracker, GameDescribe.

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

        // The villager this entry is filed under in ByVillager (zero if none).
        internal IntPtr VillagerKey;

        // "Standing still on the way to the drop point" tracking (BeforeFsmTick):
        // the item being carried, where the villager was when the timer
        // started, when it started, and when the FSM last ticked for them.
        internal IntPtr WalkItem;
        internal UnityEngine.Vector3 StillAnchor;
        internal float StillSince;
        internal float LastTickAt;
    }

    // A villager holding the same pick, going to the same place, for this long
    // is treated as stuck. The longest healthy run in the 0.3.0 log moved to its
    // next item within about 10 seconds.
    private const float StallSeconds = 30f;

    // The two minimumDistance values BeforeFsmTick hands out. The game drops
    // the item when RemainingDistance <= minimumDistance, and a remaining
    // distance is never negative.
    private const float KeepWalking = -1f;
    private const float DropNow = float.PositiveInfinity;

    // A villager who moves less than StillMoveMetres for StillSeconds on the
    // way to the drop point is not going to get any closer.
    private const float StillMoveMetres = 1f;
    private const float StillSeconds = 10f;

    // Standing still within this many metres still counts as "at the drop
    // point" (the 0.6.3 log has a villager stopping 7.8 m from the marker).
    // Further away, the drop is cancelled instead.
    private const float StuckDropMetres = 12f;

    // A longer gap than this between two FSM ticks means the cleanup was
    // interrupted, so the standing-still timer starts again.
    private const float TickGapSeconds = 2f;

    // data pointer -> watched entry. A villager's FSM reuses one data object,
    // so this stays roughly one entry per villager.
    private static readonly Dictionary<IntPtr, Watched> ByPointer = new();

    // villager pointer -> the entry for the data object that villager scanned
    // with most recently, i.e. the one their current cleanup pass is using.
    private static readonly Dictionary<IntPtr, Watched> ByVillager = new();

    // Hand-off between BeforeFsmTick and AfterFsmTick, for the one FSM tick in
    // progress. FSM ticks never nest, and all run on Unity's main thread.
    private static Watched _tickWatched;
    private static IntPtr _tickTargetBefore;
    private static bool _scanRanThisTick;

    /// <summary>
    /// Starts watching a cleanup data object, and files it as its villager's
    /// current one. Cheap when already watched, which matters because it is
    /// called for every item of every scan.
    /// Called by: CleanupCheckItemBehaviourPatch.Prefix.
    /// </summary>
    /// <param name="data">The villager's cleanup FSM state.</param>
    internal static void Register(FSM_CleanupInventory.CleanupInventoryData data)
    {
        if (data == null) return;
        var key = data.Pointer;
        if (key == IntPtr.Zero) return;

        // CheckItem only runs while the FSM scans the inventory.
        _scanRanThisTick = true;

        if (!ByPointer.TryGetValue(key, out var watched))
        {
            watched = new Watched { Data = data };
            ByPointer[key] = watched;
        }

        try
        {
            var villager = data.agent;
            if (villager == null) return;
            watched.VillagerKey = villager.Pointer;
            ByVillager[watched.VillagerKey] = watched;
        }
        catch
        {
            // An unreadable agent only means the drop-point rules skip this villager.
        }
    }

    /// <summary>
    /// Runs before each cleanup FSM tick for one villager. Redirects a ground
    /// drop to the drop point, and decides how "arrived" is judged this tick
    /// (see "Only at the drop point" in the file header).
    /// Called by: DropPointOnlyPatch.Prefix.
    /// Calls: TryRedirect, DropPoint.IsMarker, CancelDrop.
    /// </summary>
    /// <param name="villager">The villager whose cleanup FSM is about to tick.</param>
    /// <returns>
    /// The minimumDistance the FSM must use for this tick (KeepWalking or
    /// DropNow), or null to leave the game's own value in place.
    /// </returns>
    internal static float? BeforeFsmTick(SSSGame.Villager villager)
    {
        _tickWatched = null;
        _tickTargetBefore = IntPtr.Zero;
        _scanRanThisTick = false;

        if (villager == null || !Plugin.OnlyDropAtDropPoint.Value || !EyeOfOdinEnabled()) return null;
        if (!ByVillager.TryGetValue(villager.Pointer, out var watched)) return null;
        if (VoyageGuard.IsExempt(villager)) return null;

        var data = watched.Data;
        var target = data.workstationTransform;
        _tickWatched = watched;
        _tickTargetBefore = target == null ? IntPtr.Zero : target.Pointer;

        var now = UnityEngine.Time.realtimeSinceStartup;
        var interrupted = now - watched.LastTickAt > TickGapSeconds;
        watched.LastTickAt = now;

        // Only a ground drop is of interest: an item picked, no storage chosen
        // for it, and somewhere to walk to.
        var item = data.itemToDrop;
        if (item == null || data.interaction != null || target == null) return null;

        TryRedirect(data, villager, target);
        var marker = data.workstationTransform;
        if (!DropPoint.IsMarker(marker)) return null;   // no drop point found: vanilla decides

        var position = villager.transform.position;
        var distance = HorizontalDistance(position, marker.position);
        if (distance <= Plugin.DropRadius.Value) return DropNow;

        // Still on the way. Restart the standing-still timer whenever the
        // villager carries a different item, comes back from an interruption,
        // or has actually moved.
        var moved = HorizontalDistance(position, watched.StillAnchor) > StillMoveMetres;
        if (watched.WalkItem != item.Pointer || interrupted || moved)
        {
            watched.WalkItem = item.Pointer;
            watched.StillAnchor = position;
            watched.StillSince = now;
            return KeepWalking;
        }
        if (now - watched.StillSince < StillSeconds) return KeepWalking;

        // Stood still for too long: they cannot get any closer.
        watched.WalkItem = IntPtr.Zero;
        if (distance <= StuckDropMetres)
        {
            DiagnosticLog.Write("drop_point_stuck_drop",
                $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(item)} metres_from_drop_point={distance:0.#}");
            return DropNow;
        }

        CancelDrop(data, villager, item, $"drop_point_unreachable metres_from_drop_point={distance:0.#}");
        return null;
    }

    /// <summary>
    /// Runs after each cleanup FSM tick. Cancels a drop the game is about to
    /// make on the spot: the tick scanned the inventory and picked an item, but
    /// chose neither a storage nor a new walk target for it.
    /// Called by: DropPointOnlyPatch.Postfix.
    /// Calls: DropPoint.IsMarker, CancelDrop.
    /// </summary>
    internal static void AfterFsmTick()
    {
        var watched = _tickWatched;
        _tickWatched = null;
        if (watched == null || !_scanRanThisTick) return;

        var data = watched.Data;
        var item = data.itemToDrop;
        if (item == null || data.interaction != null) return;

        // A ground drop the game will walk to always gets a fresh walk target
        // on the tick that picks the item.
        var target = data.workstationTransform;
        var targetAfter = target == null ? IntPtr.Zero : target.Pointer;
        if (targetAfter != _tickTargetBefore) return;

        // An unchanged target that is not ours was never redirected (no drop
        // point exists), so that villager is on vanilla rules: leave it.
        if (target != null && !DropPoint.IsMarker(target)) return;

        CancelDrop(data, data.agent, item, "drop_on_the_spot");
    }

    /// <summary>
    /// Calls off the drop of the picked item. With no item and the NO_ITEM
    /// priority, both of the game's drop states end the cleanup run without
    /// dropping anything (read from OnStateUpdate). The item is then held for
    /// a while so the next scan does not pick it straight back up.
    /// Called by: BeforeFsmTick, AfterFsmTick. Calls: KeepRules.Hold.
    /// </summary>
    /// <param name="data">The villager's cleanup FSM state.</param>
    /// <param name="villager">The villager cleaning up.</param>
    /// <param name="item">The item that was about to be dropped.</param>
    /// <param name="reason">Why, for the log.</param>
    private static void CancelDrop(FSM_CleanupInventory.CleanupInventoryData data, SSSGame.Villager villager,
        SandSailorStudio.Inventory.Item item, string reason)
    {
        KeepRules.Hold(villager, item);
        data.itemToDrop = null;
        data.itemToDropPriority = FSM_CleanupInventory.NicePriority.NO_ITEM;
        DiagnosticLog.Write("drop_cancelled",
            $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(item)} reason={reason}");
    }

    /// <summary>Distance between two points ignoring height, in metres.</summary>
    private static float HorizontalDistance(UnityEngine.Vector3 from, UnityEngine.Vector3 to)
    {
        var deltaX = from.x - to.x;
        var deltaZ = from.z - to.z;
        return UnityEngine.Mathf.Sqrt(deltaX * deltaX + deltaZ * deltaZ);
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
        foreach (var key in dead)
        {
            // Unfile it from its villager too, unless a newer entry took that place.
            var watched = ByPointer[key];
            if (ByVillager.TryGetValue(watched.VillagerKey, out var filed) && ReferenceEquals(filed, watched))
                ByVillager.Remove(watched.VillagerKey);
            ByPointer.Remove(key);
        }
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

        // Normally BeforeFsmTick has already redirected; this is the fallback
        // for a villager it does not cover.
        if (outcome == "ground_drop" && EyeOfOdinEnabled() && !VoyageGuard.IsExempt(villager))
            TryRedirect(data, villager, target);

        // Build the state from the values after any redirect, so a redirect is
        // logged once, not every frame.
        target = data.workstationTransform;
        var redirected = outcome == "ground_drop" && DropPoint.IsMarker(target);
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

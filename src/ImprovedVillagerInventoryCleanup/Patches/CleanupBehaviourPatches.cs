// CleanupBehaviourPatches - the patches that make villagers actually put stale
// tools away, as opposed to the Diagnostics patches that only watch.
//
// Role in the larger system
// -------------------------
// ASKA's cleanup FSM (FSM_CleanupInventory) walks a villager's inventory with
// CleanupInventoryData.CheckItem, keeps the single "least important" item it
// saw in itemToDrop / itemToDropPriority, and then carries that item to storage
// or drops it on the ground.
//
// Why 0.2.0 did nothing (read from the game binary with scripts/disasm.ps1):
//
//   1. CheckItem ALWAYS returns false. Its answer is written into itemToDrop,
//      not returned, so every "accepted=False" in the 0.2.0 log was meaningless.
//   2. depositAny, the switch 0.2.0 forced on, is only read by CheckItem when
//      onlyAllowDepositingToStorage is also true - and that was False on every
//      check in the log. So forcing it changed nothing about eligibility.
//   3. The real gate is this, near the top of CheckItem:
//
//        if (!_CheckContainerNeedingSpace(item.container)
//            && !dropWithoutReasonItems.Check(item.info))
//            return false;   // item never becomes a candidate
//
//      dropWithoutReasonItems is built from the authored itemsToDropWithoutReason
//      tables, which list materials only (Fibers, Leather, Iron, Compost...).
//      Tools are not in it, so unless a bag is full a tool is rejected here,
//      before any of the priority ranking runs. That matches the log exactly:
//      235 tool checks, every one ending with the tool never selected.
//
// The fix: for items ItemEligibility allows (every category except food,
// water, bags, clothing, weapons and torches by default), make
// _CheckContainerNeedingSpace report true for that one call. That is the only
// caller of _CheckContainerNeedingSpace (confirmed with an X query), and its
// result is only used for this gate, so nothing else changes. Everything after
// the gate is still vanilla: neverDropFilter, equipped-gear, bags, warrior
// weapons and "needed at work" all still raise the item's NicePriority.
//
// On top of that we only keep an admitted item as the pick when vanilla ranked
// it 0 - "no reason at all to keep this" - or when it is an equipped tool the
// job does not need (see IsAcceptable). Anything the current job needs ranks
// NEEDED_AT_WORK, and vanilla would happily pick it as the "least bad" item if
// nothing lower existed. We undo that pick, so an admitted item is only ever
// removed when the game itself says the job does not need it.
//
// Villagers away on a karvi voyage (VoyageGuard) are skipped entirely.
//
// depositAny is still forced on, because it IS read once more after the scan:
// with it on, the picked item goes to the "deposit into storage" branch; with
// it off, a tool (not in the deposit-to-storage tables) would skip straight to
// "carry to a storage and drop it on the ground next to it".
//
// Depends on: HarmonyLib, Plugin (config), ItemEligibility, CleanupScanContext,
// DiagnosticLog, DiagnosticTracker, GameDescribe.

using System;
using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Behaviour;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame.AI.FSM;

namespace ImprovedVillagerInventoryCleanup.Patches;

/// <summary>
/// Forces <c>CleanupInventoryData.depositAny</c> on, so an item picked by the
/// scan is sent to the storage-deposit branch rather than the ground-drop one.
/// </summary>
/// <remarks>
/// OnStateUpdate re-reads the authored value into depositAny at the start of
/// every scan, so the flag is re-applied before each CheckItem call rather than
/// once. It is read after the scan finishes, by which point our write has won.
/// </remarks>
internal static class DepositAnyOverride
{
    /// <summary>
    /// Sets <c>depositAny</c> (and optionally clears
    /// <c>onlyAllowDepositingToStorage</c>) when tool depositing is enabled.
    /// Never throws: it runs inside the villager AI tick, so a failure degrades
    /// to vanilla behaviour instead of breaking the AI.
    /// Called by: <see cref="CleanupCheckItemBehaviourPatch"/> and the storage
    /// predicate prefixes below.
    /// </summary>
    /// <param name="data">The per-villager cleanup FSM state being evaluated.</param>
    internal static void Apply(FSM_CleanupInventory.CleanupInventoryData data)
    {
        if (data == null) return;
        if (!Plugin.EnableImprovedCleanup.Value) return;
        if (VoyageGuard.IsExempt(data.agent)) return;

        try
        {
            if (!data.depositAny) data.depositAny = true;

            // The FSM sets onlyAllowDepositingToStorage when a villager is
            // cleaning up because of trash (not because a cleanup was
            // requested). With it set, no ground drop is allowed.
            if (Plugin.AllowGroundDrops.Value && data.onlyAllowDepositingToStorage)
                data.onlyAllowDepositingToStorage = false;
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("deposit_any_override_failed", exception);
        }
    }
}

/// <summary>
/// What CheckItem's selection looked like before it ran, so a pick we do not
/// want can be put back exactly as it was.
/// </summary>
internal sealed class CheckItemSnapshot
{
    internal Item PreviousItem;
    internal FSM_CleanupInventory.NicePriority PreviousPriority;
    internal int PreviousNeededAtWorkCount;
    internal int PreviousNeededForQuestCount;
}

/// <summary>
/// Wraps vanilla <c>CheckItem</c>: arms the tool admission before it runs, and
/// vets the result after.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.CheckItem), new[] { typeof(Item) })]
internal static class CleanupCheckItemBehaviourPatch
{
    /// <summary>
    /// Runs before vanilla CheckItem. Records the current pick, forces
    /// depositAny, registers this data object with the CleanupWatcher (which follows
    /// what the villager does with the pick), and arms the admission when the item is in an
    /// eligible category.
    /// Calls: DepositAnyOverride.Apply, CleanupWatcher.Register,
    /// ItemEligibility.IsEligible.
    /// </summary>
    /// <param name="__instance">The villager's cleanup FSM state.</param>
    /// <param name="__0">The inventory item being considered.</param>
    /// <param name="__state">Receives the pre-call snapshot for the postfix.</param>
    private static void Prefix(FSM_CleanupInventory.CleanupInventoryData __instance, Item __0, out CheckItemSnapshot __state)
    {
        __state = null;
        CleanupScanContext.AdmissionPending = false;
        CleanupScanContext.AdmissionUsed = false;
        if (__instance == null) return;

        // A crew away on a voyage gets pure vanilla behaviour.
        if (VoyageGuard.IsExempt(__instance.agent)) return;

        DepositAnyOverride.Apply(__instance);
        CleanupWatcher.Register(__instance);

        if (!Plugin.EnableImprovedCleanup.Value || !ItemEligibility.IsEligible(__0)) return;

        try
        {
            __state = new CheckItemSnapshot
            {
                PreviousItem = __instance.itemToDrop,
                PreviousPriority = __instance.itemToDropPriority,
                PreviousNeededAtWorkCount = __instance._neededAtWorkCount,
                PreviousNeededForQuestCount = __instance._neededForQuestCount,
            };
            CleanupScanContext.AdmissionPending = true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("tool_admission_prefix_failed", exception);
        }
    }

    /// <summary>
    /// Runs after vanilla CheckItem, ahead of the diagnostic postfix so that
    /// the log shows the final pick. If our admission let an item through but
    /// vanilla ranked it above 0 (equipped, needed at work, ...), the pick is
    /// reverted to whatever it was before this call.
    /// Calls: CleanupScanContext, DiagnosticTracker.IsTracked, DiagnosticLog.
    /// </summary>
    /// <param name="__instance">The villager's cleanup FSM state.</param>
    /// <param name="__0">The inventory item that was considered.</param>
    /// <param name="__state">The snapshot taken by the prefix (null if not armed).</param>
    [HarmonyPriority(Priority.High)]
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, Item __0, CheckItemSnapshot __state)
    {
        var admitted = CleanupScanContext.AdmissionUsed;
        CleanupScanContext.AdmissionPending = false;
        CleanupScanContext.AdmissionUsed = false;
        if (!admitted || __state == null || __instance == null || __0 == null) return;

        try
        {
            var picked = __instance.itemToDrop;
            var pickedThisItem = picked != null && picked.Pointer == __0.Pointer;
            if (!pickedThisItem) return;

            var villager = __instance.agent;
            var priority = __instance.itemToDropPriority;
            if (IsAcceptable(villager, __0, priority))
            {
                if (DiagnosticTracker.IsTracked(villager))
                    DiagnosticLog.Write("item_admission_selected",
                        $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(__0)} vanilla_priority={priority}");
                return;
            }

            // Vanilla found a reason to keep this item. Put the old pick back.
            __instance.itemToDrop = __state.PreviousItem;
            __instance.itemToDropPriority = __state.PreviousPriority;
            __instance._neededAtWorkCount = __state.PreviousNeededAtWorkCount;
            __instance._neededForQuestCount = __state.PreviousNeededForQuestCount;

            if (DiagnosticTracker.IsTracked(villager))
                DiagnosticLog.Write("item_admission_reverted",
                    $"{GameDescribe.Villager(villager)} item={GameDescribe.Item(__0)} vanilla_priority={priority}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("item_admission_postfix_failed", exception);
        }
    }

    /// <summary>
    /// Decides whether an item this mod let past the gate may stay as the pick,
    /// given the priority the game ranked it at.
    /// <list type="bullet">
    /// <item>0 - the game found no reason at all to keep it: accept.</item>
    /// <item>EQUIPPED_EQUIPMENT on a tool: accept when CleanEquippedTools is on
    /// and the villager is not a warrior. In CheckItem the "needed at work"
    /// test runs after the equipped test and overrides it with NEEDED_AT_WORK,
    /// so an equipped tool that is still ranked EQUIPPED_EQUIPMENT is one the
    /// current job does not need (read from the binary). This is the builder
    /// who became a woodcutter with the hammer still in hand.</item>
    /// <item>Anything else (needed at work, bags, quest items...): reject.</item>
    /// </list>
    /// Called by: Postfix.
    /// </summary>
    /// <param name="villager">The villager cleaning up.</param>
    /// <param name="item">The picked item.</param>
    /// <param name="priority">The game's ranking for it.</param>
    private static bool IsAcceptable(SSSGame.Villager villager, Item item, FSM_CleanupInventory.NicePriority priority)
    {
        if ((int)priority == 0) return true;
        if (priority != FSM_CleanupInventory.NicePriority.EQUIPPED_EQUIPMENT) return false;
        if (!Plugin.CleanEquippedTools.Value || !ItemEligibility.IsTool(item)) return false;
        try
        {
            return villager != null && !villager.IsWarrior;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The admission itself. Makes CheckItem's "does this item's bag need space?"
/// test answer yes for an armed item, which lets it past the
/// dropWithoutReasonItems gate described in the file header.
/// </summary>
/// <remarks>
/// CheckItem is the only caller of this method, and the answer is used for
/// nothing but that gate, so faking it for one item has no other effect.
/// </remarks>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData._CheckContainerNeedingSpace))]
internal static class CleanupContainerNeedingSpacePatch
{
    /// <summary>
    /// Flips a false result to true while an admission is armed, and records
    /// that it did so, so the CheckItem postfix knows the pick came from us.
    /// </summary>
    /// <param name="__result">Vanilla's answer; overwritten when admitting.</param>
    private static void Postfix(ref bool __result)
    {
        if (!CleanupScanContext.AdmissionPending || __result) return;
        __result = true;
        CleanupScanContext.AdmissionUsed = true;
    }
}

/// <summary>
/// Applies depositAny when the FSM judges a storage building as a destination.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.StorageToDropIntoPredicate))]
internal static class CleanupStoragePredicateBehaviourPatch
{
    private static void Prefix(FSM_CleanupInventory.CleanupInventoryData __instance) =>
        DepositAnyOverride.Apply(__instance);
}

/// <summary>
/// As above, for the resource-storage flavour of the destination check.
/// </summary>
[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.ResourceStoragePredicate))]
internal static class CleanupResourceStoragePredicateBehaviourPatch
{
    private static void Prefix(FSM_CleanupInventory.CleanupInventoryData __instance) =>
        DepositAnyOverride.Apply(__instance);
}

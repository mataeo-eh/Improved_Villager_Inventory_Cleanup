using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI.FSM;

namespace ImprovedVillagerInventoryCleanup.Patches;

[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.CheckItem), new[] { typeof(Item) })]
internal static class CleanupCheckItemPatch
{
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, Item __0, bool __result)
    {
        var villager = __instance?.agent;
        if (!DiagnosticTracker.IsTracked(villager)) return;
        DiagnosticLog.Write("cleanup_fsm_check_item",
            $"{GameDescribe.Villager(villager)} accepted={__result} item={GameDescribe.Item(__0)} " +
            $"selected_priority={__instance.itemToDropPriority} only_storage={__instance.onlyAllowDepositingToStorage} " +
            $"deposit_any={__instance.depositAny} keep_any_fuel={__instance.keepAnyFuel} " +
            $"workstation_check={GameDescribe.Workstation(__instance.workstationToCheckWith)}");
    }
}

[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.StorageToDropIntoPredicate))]
internal static class CleanupStoragePredicatePatch
{
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, StorageInteraction __0, bool __result)
    {
        var villager = __instance?.agent;
        if (!DiagnosticTracker.IsTracked(villager)) return;
        DiagnosticLog.Write("cleanup_storage_candidate",
            $"{GameDescribe.Villager(villager)} accepted={__result} storage={GameDescribe.Workstation(__0)} " +
            $"item_to_drop={GameDescribe.Item(__instance.itemToDrop)} only_storage={__instance.onlyAllowDepositingToStorage} " +
            $"deposit_any={__instance.depositAny} {GameDescribe.StorageDecision(__0, __instance.itemToDrop)}");
    }
}

[HarmonyPatch(typeof(FSM_CleanupInventory.CleanupInventoryData), nameof(FSM_CleanupInventory.CleanupInventoryData.ResourceStoragePredicate))]
internal static class CleanupResourceStoragePredicatePatch
{
    private static void Postfix(FSM_CleanupInventory.CleanupInventoryData __instance, IResourceStorageSite __0, bool __result)
    {
        var villager = __instance?.agent;
        if (!DiagnosticTracker.IsTracked(villager)) return;
        DiagnosticLog.Write("cleanup_resource_storage_candidate",
            $"{GameDescribe.Villager(villager)} accepted={__result} storage={GameDescribe.Workstation(__0)} " +
            $"item_to_drop={GameDescribe.Item(__instance.itemToDrop)}");
    }
}

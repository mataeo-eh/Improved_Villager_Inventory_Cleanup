using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Behaviour;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;

namespace ImprovedVillagerInventoryCleanup.Patches;

[HarmonyPatch(typeof(CleanupInventoryQuest.CleanupInventoryQuestData), "_IsTrash", new[] { typeof(Item) })]
internal static class CleanupIsTrashPatch
{
    private static void Postfix(CleanupInventoryQuest.CleanupInventoryQuestData __instance, Item __0, bool __result)
    {
        var villager = __instance?.GetVillager();
        if (!DiagnosticTracker.IsTracked(villager)) return;
        DiagnosticLog.Write("cleanup_item_classified",
            $"{GameDescribe.Villager(villager)} is_trash={__result} item={GameDescribe.Item(__0)} " +
            $"cleanup_requested={__instance.CleanupRequested} trash_detected={__instance.TrashDetected} important={__instance.Important} working={__instance.IsWorking}");
    }
}

[HarmonyPatch(typeof(CleanupInventoryQuest.CleanupInventoryQuestData), nameof(CleanupInventoryQuest.CleanupInventoryQuestData.IsWhitelistedByStorage))]
internal static class CleanupStorageWhitelistPatch
{
    private static void Postfix(CleanupInventoryQuest.CleanupInventoryQuestData __instance, IResourceStorageSite __0, bool __result)
    {
        var villager = __instance?.GetVillager();
        if (!DiagnosticTracker.IsTracked(villager)) return;
        DiagnosticLog.Write("cleanup_storage_whitelist",
            $"{GameDescribe.Villager(villager)} allowed={__result} storage={GameDescribe.Workstation(__0)}");
    }
}

/// <summary>
/// Records every cleanup quest start, and puts the villager under observation if
/// they were not already.
/// </summary>
/// <remarks>
/// This is the one patch that deliberately does NOT require the villager to be
/// tracked first. Tracking otherwise only begins on assignment or release, so the
/// case we most want to study - a villager cleaning up WITHOUT a job change, as
/// Tove did in the 0.1.0 log when a scrap Iron Plate set TrashDetected - was
/// invisible. Starting a cleanup quest is now itself a reason to start watching,
/// which means the whole run that follows (item checks, storage candidates,
/// inventory snapshots) gets recorded.
/// </remarks>
[HarmonyPatch(typeof(CleanupInventoryQuest.CleanupInventoryQuestData), nameof(CleanupInventoryQuest.CleanupInventoryQuestData.Start))]
internal static class CleanupQuestStartPatch
{
    private static void Postfix(CleanupInventoryQuest.CleanupInventoryQuestData __instance)
    {
        CleanupScheduler.OnCleanupStarted(__instance);
        var villager = __instance?.GetVillager();
        if (villager == null) return;

        // Track before logging, so this start and everything after it is captured.
        // A shorter window than the assignment-triggered one keeps the log readable:
        // the longest observed cleanup run was 27 seconds.
        DiagnosticTracker.Track(villager, "cleanup_quest_started",
            Plugin.CleanupTrackingWindowSeconds.Value);

        DiagnosticLog.Write("cleanup_quest_started",
            $"{GameDescribe.Villager(villager)} cleanup_requested={__instance.CleanupRequested} trash_detected={__instance.TrashDetected} " +
            $"important={__instance.Important} working={__instance.IsWorking} inventory={GameDescribe.Inventory(villager)}");
    }
}

[HarmonyPatch(typeof(CleanupInventoryQuest.CleanupInventoryQuestData), nameof(CleanupInventoryQuest.CleanupInventoryQuestData.Stop))]
internal static class CleanupQuestStopPatch
{
    private static void Postfix(CleanupInventoryQuest.CleanupInventoryQuestData __instance) =>
        CleanupScheduler.OnCleanupStopped(__instance);

    private static void Prefix(CleanupInventoryQuest.CleanupInventoryQuestData __instance)
    {
        var villager = __instance?.GetVillager();
        if (!DiagnosticTracker.IsTracked(villager)) return;
        DiagnosticLog.Write("cleanup_quest_stopped",
            $"{GameDescribe.Villager(villager)} cleanup_requested={__instance.CleanupRequested} trash_detected={__instance.TrashDetected} " +
            $"important={__instance.Important} working={__instance.IsWorking} inventory={GameDescribe.Inventory(villager)}");
    }
}

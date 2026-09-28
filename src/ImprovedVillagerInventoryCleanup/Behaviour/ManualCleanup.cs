// ManualCleanup - starts a villager's "clean inventory" quest on demand.
//
// Role in the larger system: two callers need to make a villager clean up now.
// CleanupScheduler does it for villagers who still carry unneeded items, and
// the debug button (UI/CleanupButton) does it for the villager on screen.
//
// It does exactly what the game itself does when it wants a cleanup (read from
// the binary: CleanupInventoryQuestData._OnWorkstationEvent and
// _OnVillagerBehaviorChanged):
//
//   questData.CleanupRequested = true;     // without this GetPriority returns 0
//   questData.Important = important;       // work priority 22 instead of 15
//   questRunner.ReevaluateQuest(quest);    // re-rank the villager's quests now
//
// CleanupInventoryQuest.GetPriority returns 0 - never runs - unless
// CleanupRequested or TrashDetected is set, and Stop() clears both. So vanilla
// cleanup forgets a request as soon as a run ends or is interrupted.
//
// Depends on: DiagnosticLog, DiagnosticTracker, GameDescribe, Plugin (config), VoyageGuard.

using System;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame;
using SSSGame.AI;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class ManualCleanup
{
    /// <summary>
    /// Button entry point: requests an important cleanup and returns a message
    /// for the player. Never throws.
    /// Called by: UI.CleanupButton when the player clicks it.
    /// </summary>
    /// <param name="villager">The villager shown in the open villager menu.</param>
    /// <returns>A short message for the button to show.</returns>
    internal static string Request(Villager villager)
    {
        if (villager == null) return "No villager selected.";
        if (VoyageGuard.IsExempt(villager)) return $"{villager.GetName()} is away on a voyage; left vanilla.";

        DiagnosticTracker.Track(villager, "manual_cleanup_button", Plugin.CleanupTrackingWindowSeconds.Value);
        var requested = RequestCleanup(villager, important: true, reason: "button");
        return requested > 0
            ? $"Cleanup requested for {villager.GetName()}."
            : "This villager has no cleanup quest (no job?).";
    }

    /// <summary>
    /// Arms every cleanup quest the villager has (normally one) and asks the
    /// quest runner to re-rank. Never throws; the outcome is logged.
    /// Called by: Request, CleanupScheduler.
    /// </summary>
    /// <param name="villager">The villager to clean up.</param>
    /// <param name="important">True for the higher work priority (22 vs 15).</param>
    /// <param name="reason">Why, for the log ("button", "unneeded_items").</param>
    /// <returns>How many cleanup quests were armed; 0 means none exists.</returns>
    internal static int RequestCleanup(Villager villager, bool important, string reason)
    {
        try
        {
            var runner = villager.GetQuestRunner();
            if (runner == null) return 0;

            var requested = 0;
            foreach (var entry in runner._questDataTable)
            {
                var data = entry.Value?.TryCast<CleanupInventoryQuest.CleanupInventoryQuestData>();
                if (data == null) continue;

                data.CleanupRequested = true;
                if (important) data.Important = true;
                runner.ReevaluateQuest(entry.Key);
                requested++;

                DiagnosticLog.Write("cleanup_requested",
                    $"{GameDescribe.Villager(villager)} reason={reason} important={important} " +
                    $"quest_status={data.questStatus} quest_priority={entry.Key.GetPriority(data):0.##} " +
                    $"active_quest_after={GameDescribe.Quest(runner)}");
            }

            if (requested == 0)
                DiagnosticLog.Write("cleanup_request_failed", $"{GameDescribe.Villager(villager)} reason={reason} why=no_cleanup_quest");
            return requested;
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("cleanup_request_failed", exception);
            return 0;
        }
    }
}

// ManualCleanup - starts a villager's "clean inventory" quest on demand.
//
// Role in the larger system: testing needs a deterministic way to make one
// chosen villager clean up now, instead of changing jobs or schedules and
// hoping. The villager menu button (UI/CleanupButton) calls Request().
//
// It does exactly what the game itself does when it wants a cleanup (read from
// the binary: CleanupInventoryQuestData._OnWorkstationEvent and
// _OnVillagerBehaviorChanged):
//
//   questData.CleanupRequested = true;     // without this GetPriority returns 0
//   questData.Important = true;            // work priority 22 instead of 15
//   questRunner.ReevaluateQuest(quest);    // re-rank the villager's quests now
//
// CleanupInventoryQuest.GetPriority returns 0 - never runs - unless
// CleanupRequested or TrashDetected is set, and Stop() clears both. That is why,
// in the 0.3.0 test, cleanup ran once after loading and then almost never: a
// run that was interrupted lost its request until the next job or schedule
// change. Pressing the button again re-arms it.
//
// Depends on: DiagnosticLog, DiagnosticTracker, GameDescribe, Plugin (config).

using System;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame;
using SSSGame.AI;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class ManualCleanup
{
    /// <summary>
    /// Requests a cleanup from every cleanup quest the villager has (normally
    /// one), and puts the villager under observation so the run is logged.
    /// Never throws; the outcome is written to the diagnostic log.
    /// Called by: UI.CleanupButton when the player clicks it.
    /// </summary>
    /// <param name="villager">The villager shown in the open villager menu.</param>
    /// <returns>A short message for the button to show the player.</returns>
    internal static string Request(Villager villager)
    {
        if (villager == null) return "No villager selected.";

        try
        {
            DiagnosticTracker.Track(villager, "manual_cleanup_button", Plugin.CleanupTrackingWindowSeconds.Value);

            var runner = villager.GetQuestRunner();
            if (runner == null)
            {
                DiagnosticLog.Write("manual_cleanup_failed", $"{GameDescribe.Villager(villager)} reason=no_quest_runner");
                return "Villager has no quest runner.";
            }

            var requested = 0;
            foreach (var entry in runner._questDataTable)
            {
                var data = entry.Value?.TryCast<CleanupInventoryQuest.CleanupInventoryQuestData>();
                if (data == null) continue;

                data.CleanupRequested = true;
                data.Important = true;
                runner.ReevaluateQuest(entry.Key);
                requested++;

                DiagnosticLog.Write("manual_cleanup_requested",
                    $"{GameDescribe.Villager(villager)} quest_status={data.questStatus} " +
                    $"quest_priority={entry.Key.GetPriority(data):0.##} " +
                    $"active_quest_after={GameDescribe.Quest(runner)} inventory={GameDescribe.Inventory(villager)}");
            }

            if (requested == 0)
            {
                DiagnosticLog.Write("manual_cleanup_failed", $"{GameDescribe.Villager(villager)} reason=no_cleanup_quest");
                return "This villager has no cleanup quest (no job or home?).";
            }
            return $"Cleanup requested for {villager.GetName()}.";
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("manual_cleanup_failed", exception);
            return "Cleanup request failed - see the diagnostic log.";
        }
    }
}

using HarmonyLib;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame;
using SSSGame.AI;

namespace ImprovedVillagerInventoryCleanup.Patches;

internal sealed class AssignmentCallState
{
    internal string PreviousWorkstation;
    internal string InventoryBefore;
}

[HarmonyPatch(typeof(Villager), nameof(Villager.AssignToWorkstation), new[] { typeof(IWorkstation) })]
internal static class AssignToWorkstationPatch
{
    private static void Prefix(Villager __instance, IWorkstation __0, out AssignmentCallState __state)
    {
        __state = new AssignmentCallState
        {
            PreviousWorkstation = GameDescribe.Workstation(__instance?.GetWorkstation()),
            InventoryBefore = GameDescribe.Inventory(__instance)
        };
        DiagnosticTracker.Track(__instance, "assign_to_workstation_called");
        DiagnosticLog.Write("assignment_requested",
            $"{GameDescribe.Villager(__instance)} previous_workstation={__state.PreviousWorkstation} " +
            $"requested_workstation={GameDescribe.Workstation(__0)} inventory_before={__state.InventoryBefore}");
    }

    private static void Postfix(Villager __instance, IWorkstation __0, bool __result, AssignmentCallState __state)
    {
        DiagnosticLog.Write("assignment_result",
            $"{GameDescribe.Villager(__instance)} success={__result} previous_workstation={__state?.PreviousWorkstation} " +
            $"requested_workstation={GameDescribe.Workstation(__0)} current_workstation={GameDescribe.Workstation(__instance?.GetWorkstation())} " +
            $"inventory_after={GameDescribe.Inventory(__instance)}");
    }
}

[HarmonyPatch(typeof(Villager), "_OnWorkstationChanged")]
internal static class WorkstationChangedPatch
{
    private static void Postfix(Villager __instance, IWorkstation __0, bool __1)
    {
        DiagnosticTracker.Track(__instance, "workstation_changed_event");
        DiagnosticLog.Write("workstation_changed",
            $"{GameDescribe.Villager(__instance)} workstation={GameDescribe.Workstation(__0)} is_assignment={__1} " +
            $"inventory={GameDescribe.Inventory(__instance)}");
    }
}

[HarmonyPatch(typeof(Villager), nameof(Villager.ReleaseFromWorkstation), new[] { typeof(IWorkstation), typeof(int) })]
internal static class ReleaseFromWorkstationPatch
{
    private static void Prefix(Villager __instance, IWorkstation __0, int __1)
    {
        DiagnosticTracker.Track(__instance, "release_from_workstation_called");
        DiagnosticLog.Write("workstation_release_requested",
            $"{GameDescribe.Villager(__instance)} workstation={GameDescribe.Workstation(__0)} reason={__1} inventory={GameDescribe.Inventory(__instance)}");
    }

    private static void Postfix(Villager __instance, bool __result) =>
        DiagnosticLog.Write("workstation_release_result",
            $"{GameDescribe.Villager(__instance)} success={__result} current_workstation={GameDescribe.Workstation(__instance?.GetWorkstation())} " +
            $"inventory={GameDescribe.Inventory(__instance)}");
}

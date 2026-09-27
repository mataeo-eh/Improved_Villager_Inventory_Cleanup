// VillagerMenuPatches - tells UI/CleanupButton when a villager's menu opens
// and closes.
//
// SSSGame.UI.VillagerMenu is the menu opened by interacting with a villager
// (details, schedule, happiness, "dismiss from job/home"). It is a
// SandSailorStudio Menu, so OnActivate runs when it is shown and
// OnDeactivate / OnClosed when it goes away. The menu's GetVillager() follows
// its next/previous villager buttons, so the button always acts on whoever is
// on screen.
//
// These patches only record the menu; they change nothing about it.

using HarmonyLib;
using ImprovedVillagerInventoryCleanup.UI;
using SSSGame.UI;

namespace ImprovedVillagerInventoryCleanup.Patches;

[HarmonyPatch(typeof(VillagerMenu), nameof(VillagerMenu.OnActivate))]
internal static class VillagerMenuActivatePatch
{
    private static void Postfix(VillagerMenu __instance) => CleanupButton.OnMenuOpened(__instance);
}

[HarmonyPatch(typeof(VillagerMenu), nameof(VillagerMenu.OnDeactivate))]
internal static class VillagerMenuDeactivatePatch
{
    private static void Postfix(VillagerMenu __instance) => CleanupButton.OnMenuClosed(__instance);
}

[HarmonyPatch(typeof(VillagerMenu), nameof(VillagerMenu.OnClosed))]
internal static class VillagerMenuClosedPatch
{
    private static void Postfix(VillagerMenu __instance) => CleanupButton.OnMenuClosed(__instance);
}

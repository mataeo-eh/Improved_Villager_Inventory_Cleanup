// CleanupButton - a "Clean inventory now" button shown while a villager's menu
// is open.
//
// Role in the larger system: a test tool. Clicking it calls
// ManualCleanup.Request for the villager the menu is showing, so the cleanup
// quest starts on demand instead of after a job or schedule change.
//
// It is drawn with Unity's immediate-mode GUI (OnGUI) on top of the game's
// villager menu, rather than cloned into the menu's own layout. That keeps it
// independent of the menu prefab, which is not visible in the game binary.
// The menu hooks are in Patches/VillagerMenuPatches.cs.
//
// Depends on: ManualCleanup, Plugin (config), SSSGame.UI.VillagerMenu, UnityEngine IMGUI.

using System;
using ImprovedVillagerInventoryCleanup.Behaviour;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame.UI;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.UI;

internal static class CleanupButton
{
    // The villager menu currently open, or null when none is.
    private static VillagerMenu _openMenu;

    // Feedback shown under the button after a click, and when it expires.
    private static string _message;
    private static float _messageUntil;

    /// <summary>Remembers the open villager menu. Called by the VillagerMenu.OnActivate postfix.</summary>
    /// <param name="menu">The menu that just opened.</param>
    internal static void OnMenuOpened(VillagerMenu menu) => _openMenu = menu;

    /// <summary>Forgets the villager menu. Called by the OnDeactivate / OnClosed postfixes.</summary>
    /// <param name="menu">The menu that just closed.</param>
    internal static void OnMenuClosed(VillagerMenu menu)
    {
        if (_openMenu != null && menu != null && _openMenu.Pointer == menu.Pointer) _openMenu = null;
    }

    /// <summary>
    /// Draws the button while a villager menu is open, and handles clicks.
    /// Never throws, since it runs every GUI event.
    /// Called by: DiagnosticBehaviour.OnGUI.
    /// </summary>
    internal static void Draw()
    {
        if (!Plugin.ShowCleanupButton.Value || _openMenu == null) return;

        try
        {
            // A menu destroyed without a close callback reads as null here.
            if (_openMenu == null || !_openMenu.isActiveAndEnabled) return;

            var villager = _openMenu.GetVillager();
            if (villager == null) return;

            var width = 260f;
            var height = 40f;
            var x = Screen.width * Plugin.CleanupButtonX.Value - width / 2f;
            var y = Screen.height * Plugin.CleanupButtonY.Value;

            if (GUI.Button(new Rect(x, y, width, height), $"Clean inventory now ({villager.GetName()})"))
            {
                _message = ManualCleanup.Request(villager);
                _messageUntil = Time.realtimeSinceStartup + 4f;
            }

            if (_message != null && Time.realtimeSinceStartup < _messageUntil)
                GUI.Label(new Rect(x, y + height + 4f, width + 200f, 24f), _message);
        }
        catch (Exception exception)
        {
            _openMenu = null;
            DiagnosticLog.WriteException("cleanup_button_failed", exception);
        }
    }
}

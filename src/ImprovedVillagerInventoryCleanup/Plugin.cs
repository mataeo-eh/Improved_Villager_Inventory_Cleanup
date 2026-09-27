using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using ImprovedVillagerInventoryCleanup.Behaviour;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "aska.improved.villager.inventory.cleanup";
    public const string PluginName = "Improved Villager Inventory Cleanup";
    public const string PluginVersion = "0.4.0";

    internal static new ManualLogSource Log { get; private set; }
    internal static ConfigEntry<float> TrackingWindowSeconds { get; private set; }
    internal static ConfigEntry<float> SnapshotIntervalSeconds { get; private set; }
    internal static ConfigEntry<float> CleanupTrackingWindowSeconds { get; private set; }

    // Behaviour switches. Every one of these can be turned off to fall back to
    // vanilla without rebuilding, which matters because this is the first version
    // that changes the game rather than only watching it.
    internal static ConfigEntry<bool> EnableToolDepositing { get; private set; }
    internal static ConfigEntry<bool> AllowGroundDrops { get; private set; }
    internal static ConfigEntry<string> EligibleCategories { get; private set; }

    // Eye of Odin fallback: a test mode that proves cleanup works regardless of
    // storage space, by having villagers drop items in front of the Eye.
    internal static ConfigEntry<bool> EyeOfOdinFallback { get; private set; }
    internal static ConfigEntry<bool> ForceEyeOfOdinDrops { get; private set; }
    internal static ConfigEntry<float> EyeOfOdinStandOffDistance { get; private set; }

    // Test tool: the "Clean inventory now" button in the villager menu.
    internal static ConfigEntry<bool> ShowCleanupButton { get; private set; }
    internal static ConfigEntry<float> CleanupButtonX { get; private set; }
    internal static ConfigEntry<float> CleanupButtonY { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Log = base.Log;
        TrackingWindowSeconds = Config.Bind("Diagnostics", "TrackingWindowSeconds", 600f,
            "How long a villager remains under detailed observation after a workstation change.");
        SnapshotIntervalSeconds = Config.Bind("Diagnostics", "SnapshotIntervalSeconds", 5f,
            "How often an observed villager's inventory and active quest are recorded.");
        CleanupTrackingWindowSeconds = Config.Bind("Diagnostics", "CleanupTrackingWindowSeconds", 180f,
            "How long a villager is observed after STARTING a cleanup quest, as opposed to after a " +
            "workstation change. This is what captures villagers who clean up without being reassigned. " +
            "Kept shorter than TrackingWindowSeconds because the longest observed cleanup run was 27 " +
            "seconds, and a long window across many villagers makes the log hard to read.");

        EnableToolDepositing = Config.Bind("Cleanup", "EnableToolDepositing", true,
            "Let villagers put away items vanilla never considers, most importantly tools they no longer need. " +
            "Turns on the game's own 'deposit anything' switch, so vanilla's protections for equipped gear, " +
            "bags, food and job-required items all still apply. Set false to restore vanilla behaviour exactly.");
        AllowGroundDrops = Config.Bind("Cleanup", "AllowGroundDrops", true,
            "Allow dropping an unwanted item on the ground when no storage will accept it. " +
            "Without this, a villager with no valid storage keeps carrying the item forever.");
        EligibleCategories = Config.Bind("Cleanup", "EligibleCategories", "Tools",
            "Comma-separated item category paths villagers may put away when unneeded, on top of what vanilla allows. " +
            "A path covers everything under it: 'Tools' covers 'Tools/Axes', 'Tools/Hoes' and so on. " +
            "Items the villager's current job needs, or has equipped, are always kept.");
        EligibleCategories.SettingChanged += OnEligibleCategoriesChanged;
        ItemEligibility.Reload();

        EyeOfOdinFallback = Config.Bind("EyeOfOdin", "DropAtEyeOfOdinWhenNoStorage", true,
            "When no storage will take an item, walk to the Eye of Odin and drop it on the ground in front of it, " +
            "instead of dropping it at the villager's workstation.");
        ForceEyeOfOdinDrops = Config.Bind("EyeOfOdin", "ForceEyeOfOdinDrops", true,
            "TEST MODE. Skip storage entirely: everything villagers would have put into storage while cleaning up " +
            "is dropped on the ground in front of the Eye of Odin instead. Proves cleanup is working independently " +
            "of storage space. Turn off to deposit into storage normally.");
        EyeOfOdinStandOffDistance = Config.Bind("EyeOfOdin", "StandOffDistance", 4f,
            "How many metres in front of the Eye of Odin villagers stand to drop items. " +
            "Use a negative value if they end up behind it.");

        ShowCleanupButton = Config.Bind("TestTools", "ShowCleanupButton", true,
            "Show a 'Clean inventory now' button while a villager's menu is open. Clicking it starts that " +
            "villager's cleanup quest immediately, the same way a job change does.");
        CleanupButtonX = Config.Bind("TestTools", "CleanupButtonX", 0.5f,
            "Horizontal centre of the button, as a fraction of screen width (0 = left edge, 1 = right edge).");
        CleanupButtonY = Config.Bind("TestTools", "CleanupButtonY", 0.04f,
            "Top of the button, as a fraction of screen height (0 = top edge, 1 = bottom edge).");
        DiagnosticLog.Initialize();
        try
        {
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();

            ClassInjector.RegisterTypeInIl2Cpp<DiagnosticBehaviour>();
            var host = new GameObject("ImprovedVillagerInventoryCleanup.Diagnostics");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<DiagnosticBehaviour>();

            DiagnosticLog.Write("plugin_loaded",
                $"version={PluginVersion} game_version={Application.version} " +
                $"tracking_seconds={TrackingWindowSeconds.Value:0.###} " +
                $"snapshot_seconds={SnapshotIntervalSeconds.Value:0.###} " +
                $"cleanup_tracking_seconds={CleanupTrackingWindowSeconds.Value:0.###} " +
                $"tool_depositing={EnableToolDepositing.Value} ground_drops={AllowGroundDrops.Value} " +
                $"eligible_categories={DiagnosticLog.Quote(EligibleCategories.Value)} " +
                $"eye_of_odin_fallback={EyeOfOdinFallback.Value} force_eye_of_odin={ForceEyeOfOdinDrops.Value}");
            Log.LogInfo($"{PluginName} {PluginVersion} loaded (tool depositing={EnableToolDepositing.Value}, " +
                        $"eye of odin fallback={EyeOfOdinFallback.Value}, forced={ForceEyeOfOdinDrops.Value}).");
            Log.LogInfo($"Diagnostic output: {DiagnosticLog.OutputPath}");
        }
        catch (Exception exception)
        {
            Log.LogError($"Failed to start diagnostics: {exception}");
            DiagnosticLog.WriteException("plugin_start_failed", exception);
        }
    }

    /// <summary>
    /// Re-reads the eligible category list when it is edited while the game runs.
    /// Subscribed to <c>EligibleCategories.SettingChanged</c> in <see cref="Load"/>.
    /// </summary>
    private static void OnEligibleCategoriesChanged(object sender, EventArgs args) => ItemEligibility.Reload();

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        DiagnosticLog.Write("plugin_unloaded", "normal=true");
        DiagnosticLog.Shutdown();
        return true;
    }
}


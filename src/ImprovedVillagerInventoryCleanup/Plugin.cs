// Plugin - BepInEx entry point for Improved Villager Inventory Cleanup.
//
// Role in the larger system: binds every config entry, applies the Harmony
// patches in Patches/, and creates the one hidden Unity object
// (DiagnosticBehaviour) that gives the mod a per-frame tick and a GUI pass.
//
// Config keys whose defaults changed meaning between versions were given new
// names rather than new defaults, because BepInEx keeps a value already saved
// in the .cfg file. For example 0.4.0's ForceEyeOfOdinDrops=true would otherwise
// silently keep test mode on.

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
    public const string PluginVersion = "0.6.0";

    internal static new ManualLogSource Log { get; private set; }

    // Diagnostics: how much the focused log records.
    internal static ConfigEntry<float> TrackingWindowSeconds { get; private set; }
    internal static ConfigEntry<float> SnapshotIntervalSeconds { get; private set; }
    internal static ConfigEntry<float> CleanupTrackingWindowSeconds { get; private set; }

    // Cleanup: what villagers put away, and when.
    internal static ConfigEntry<bool> EnableImprovedCleanup { get; private set; }
    internal static ConfigEntry<string> EligibleCategories { get; private set; }
    internal static ConfigEntry<string> ExcludedCategories { get; private set; }
    internal static ConfigEntry<bool> CleanEquippedTools { get; private set; }
    internal static ConfigEntry<bool> KeepCleaningUntilDone { get; private set; }
    internal static ConfigEntry<float> RecheckIntervalSeconds { get; private set; }
    internal static ConfigEntry<bool> AddCleanupToStationsWithout { get; private set; }
    internal static ConfigEntry<float> StorageSearchDistance { get; private set; }
    internal static ConfigEntry<bool> AllowGroundDrops { get; private set; }

    // Last resort: where items go when no storage anywhere will take them.
    internal static ConfigEntry<bool> EyeOfOdinFallback { get; private set; }
    internal static ConfigEntry<float> EyeOfOdinStandOffDistance { get; private set; }

    // Debug: test tools, off for normal play.
    internal static ConfigEntry<bool> ForceEyeOfOdinDrops { get; private set; }
    internal static ConfigEntry<bool> ShowCleanupButton { get; private set; }
    internal static ConfigEntry<float> CleanupButtonX { get; private set; }
    internal static ConfigEntry<float> CleanupButtonY { get; private set; }

    private Harmony _harmony;

    public override void Load()
    {
        Log = base.Log;
        BindConfig();
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
                $"improved_cleanup={EnableImprovedCleanup.Value} " +
                $"eligible={DiagnosticLog.Quote(EligibleCategories.Value)} excluded={DiagnosticLog.Quote(ExcludedCategories.Value)} " +
                $"clean_equipped_tools={CleanEquippedTools.Value} keep_cleaning={KeepCleaningUntilDone.Value} " +
                $"recheck_seconds={RecheckIntervalSeconds.Value:0.#} add_missing_quests={AddCleanupToStationsWithout.Value} " +
                $"storage_search_distance={StorageSearchDistance.Value:0.#} " +
                $"last_resort_drops={EyeOfOdinFallback.Value} force_last_resort={ForceEyeOfOdinDrops.Value} " +
                $"cleanup_button={ShowCleanupButton.Value}");
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
            Log.LogInfo($"Diagnostic output: {DiagnosticLog.OutputPath}");
        }
        catch (Exception exception)
        {
            Log.LogError($"Failed to start: {exception}");
            DiagnosticLog.WriteException("plugin_start_failed", exception);
        }
    }

    /// <summary>
    /// Binds every config entry. Split out of Load only to keep Load readable.
    /// Calls: ItemEligibility.Reload.
    /// </summary>
    private void BindConfig()
    {
        TrackingWindowSeconds = Config.Bind("Diagnostics", "TrackingWindowSeconds", 600f,
            "How long a villager remains under detailed observation after a workstation change.");
        SnapshotIntervalSeconds = Config.Bind("Diagnostics", "SnapshotIntervalSeconds", 5f,
            "How often an observed villager's inventory and active quest are recorded.");
        CleanupTrackingWindowSeconds = Config.Bind("Diagnostics", "CleanupTrackingWindowSeconds", 180f,
            "How long a villager is observed after starting a cleanup quest.");

        EnableImprovedCleanup = Config.Bind("Cleanup", "EnableImprovedCleanup", true,
            "Master switch. False restores vanilla cleanup exactly.");
        EligibleCategories = Config.Bind("Cleanup", "EligibleItemCategories", "*",
            "Comma-separated item categories villagers may put away when their job does not need them. " +
            "'*' means every category. A category covers everything under it: 'Tools' covers 'Tools/Axes'. " +
            "Items the current job needs are always kept, whatever this says.");
        ExcludedCategories = Config.Bind("Cleanup", "ExcludedItemCategories",
            "Resources/Food, Resources/Elements, Bags, Armor, Weapons, Tools/Torches",
            "Comma-separated categories never put away by this mod, even when eligible above. " +
            "Defaults keep food, water and smilk, bags, clothing, weapons and torches, which villagers " +
            "need for survival, dressing, defence or light rather than for their job.");
        CleanEquippedTools = Config.Bind("Cleanup", "CleanEquippedTools", true,
            "Also put away a tool the villager has equipped (in hand) when their current job does not need it. " +
            "Never applies to warriors, whose tools can be weapons.");
        KeepCleaningUntilDone = Config.Bind("Cleanup", "KeepCleaningUntilDone", true,
            "Keep asking a villager to clean up until they carry nothing their job does not need. " +
            "Vanilla only asks on a job or schedule change, and forgets the request if the run is interrupted.");
        RecheckIntervalSeconds = Config.Bind("Cleanup", "RecheckIntervalSeconds", 20f,
            "How often every villager is checked for unneeded items. A villager whose cleanup could not " +
            "remove anything is checked less and less often, up to 10 minutes, so nobody loops.");
        AddCleanupToStationsWithout = Config.Bind("Cleanup", "AddCleanupToStationsWithout", true,
            "Give workers of stations that have no cleanup quest of their own (such as the fire and air " +
            "altars) the game's standard cleanup quest.");
        StorageSearchDistance = Config.Bind("Cleanup", "StorageSearchDistance", 100000f,
            "How far (metres) a cleaning villager will look for a storage that accepts an item. The large " +
            "default means any storage in the settlement. 0 or less keeps the game's own limit.");
        AllowGroundDrops = Config.Bind("Cleanup", "AllowGroundDrops", true,
            "Allow ground drops during cleanups the game started because of trash.");
        ItemEligibility.Reload();
        EligibleCategories.SettingChanged += OnCategoriesChanged;
        ExcludedCategories.SettingChanged += OnCategoriesChanged;

        EyeOfOdinFallback = Config.Bind("LastResort", "DropAtEyeOfOdinWhenNoStorage", true,
            "When no storage anywhere will take an item, drop it on the ground in front of the Eye of Odin. " +
            "Villagers who live at an outpost drop it in front of their outpost instead.");
        EyeOfOdinStandOffDistance = Config.Bind("LastResort", "StandOffDistance", 4f,
            "How many metres in front of the Eye of Odin (or outpost) villagers stand to drop items. " +
            "Use a negative value if they end up behind it.");

        ForceEyeOfOdinDrops = Config.Bind("Debug", "ForceLastResortDrops", false,
            "TEST ONLY. Skip storage entirely and send every cleaned-up item to the last-resort drop point.");
        ShowCleanupButton = Config.Bind("Debug", "ShowCleanupButton", false,
            "Show a 'Clean inventory now' button while a villager's menu is open.");
        CleanupButtonX = Config.Bind("Debug", "CleanupButtonX", 0.5f,
            "Horizontal centre of the button, as a fraction of screen width (0 = left, 1 = right).");
        CleanupButtonY = Config.Bind("Debug", "CleanupButtonY", 0.15f,
            "Top of the button, as a fraction of screen height (0 = top, 1 = bottom).");
    }

    /// <summary>
    /// Re-reads the category lists when either is edited while the game runs.
    /// Subscribed to both category entries' SettingChanged in BindConfig.
    /// </summary>
    private static void OnCategoriesChanged(object sender, EventArgs args) => ItemEligibility.Reload();

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        DiagnosticLog.Write("plugin_unloaded", "normal=true");
        DiagnosticLog.Shutdown();
        return true;
    }
}

// CleanupScheduler - makes sure cleanup actually happens, and keeps happening
// until a villager carries nothing their job does not need.
//
// Role in the larger system
// -------------------------
// The patches make unneeded items *eligible*; this class makes sure the
// cleanup quest *runs*. Vanilla only requests a cleanup on a job, schedule or
// viking-status change, and forgets the request when a run ends or is
// interrupted. In the 0.4.0 test that meant pressing the button several times
// to clear all of a villager's tools. Three jobs, run on a timer:
//
// 1. Keep cleaning (KeepCleaningUntilDone). Every RecheckIntervalSeconds each
//    villager's inventory is checked with the game's own test,
//    Workstation.IsItemNeededByVillager (and IsFuelNeededByVillager). If
//    anything eligible is not needed, the villager's cleanup is requested with
//    the ordinary (not "important") priority, so it slots in around work the
//    way a vanilla job-change cleanup does. A villager whose cleanup removed
//    nothing is re-checked less and less often (doubling, up to 10 minutes),
//    so an item the cleanup cannot remove never causes a loop.
//
// 2. Missing cleanup quests (AddCleanupToStationsWithout). Each station type
//    builds its own quests in Awake, and some never build a cleanup quest - the
//    fire and air altars (SSSGame.Praystation) in the 0.4.0 test. Their workers
//    get a standard CleanupInventoryQuest for their station, built with the
//    authored cleanup behaviour of a CraftingStation, exactly as
//    CraftingStation.Awake builds its own. It is removed again when they
//    change job or their new station supplies its own.
//
// 3. Storage search distance (StorageSearchDistance). The cleanup FSM passes
//    its authored maxDistanceToStorage to Settlement.FindStorageToDeposit,
//    which only reads it. Raising it on the FSM assets means "any storage in
//    the settlement", so the Eye of Odin really is the last resort.
//
// Villagers away on a karvi voyage are skipped (VoyageGuard).
//
// Depends on: Plugin (config), ItemEligibility, ManualCleanup, VoyageGuard,
// DiagnosticLog, GameDescribe.

using System;
using System.Collections.Generic;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;
using SSSGame.AI.FSM;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class CleanupScheduler
{
    /// <summary>Per-villager back-off, so an unremovable item never loops.</summary>
    private sealed class Backoff
    {
        internal float NextAllowedAt;
        internal float DelaySeconds;
        internal int CountAtLastRequest = -1;
    }

    /// <summary>A cleanup quest this mod added to one villager.</summary>
    private sealed class Injected
    {
        internal CleanupInventoryQuest Quest;
        internal IntPtr Workstation;
    }

    private const float MaxBackoffSeconds = 600f;

    // Villagers still to check in the current sweep; a few are checked per
    // frame so a sweep never causes a hitch.
    private const int VillagersPerFrame = 3;
    private static readonly Queue<Villager> Pending = new();
    private static float _nextSweepAt;

    private static readonly Dictionary<IntPtr, Backoff> BackoffByVillager = new();
    private static readonly Dictionary<IntPtr, Injected> InjectedByVillager = new();

    // One injected quest per station, shared by its workers, as vanilla does.
    private static readonly Dictionary<IntPtr, CleanupInventoryQuest> InjectedQuestByStation = new();
    private static vFSMBehaviour _cleanupBehaviour;

    // FSM assets already given the configured search distance.
    private static readonly HashSet<IntPtr> DistanceApplied = new();

    /// <summary>
    /// Advances the scheduler: starts a sweep when due, then checks a few
    /// villagers. Never throws.
    /// Called by: DiagnosticBehaviour.Update, once per frame.
    /// </summary>
    internal static void Tick()
    {
        if (!Plugin.EnableImprovedCleanup.Value) return;

        var now = Time.realtimeSinceStartup;
        if (Pending.Count == 0 && now >= _nextSweepAt)
        {
            _nextSweepAt = now + Mathf.Max(5f, Plugin.RecheckIntervalSeconds.Value);
            StartSweep();
        }

        for (var processed = 0; processed < VillagersPerFrame && Pending.Count > 0; processed++)
        {
            var villager = Pending.Dequeue();
            try
            {
                if (villager != null) CheckVillager(villager, now);
            }
            catch (Exception exception)
            {
                DiagnosticLog.WriteException("scheduler_check_failed", exception);
            }
        }
    }

    /// <summary>Queues every villager in the world and refreshes the FSM search distance.</summary>
    private static void StartSweep()
    {
        try
        {
            ApplyStorageSearchDistance();
            foreach (var villager in UnityEngine.Object.FindObjectsOfType<Villager>())
                Pending.Enqueue(villager);
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("scheduler_sweep_failed", exception);
        }
    }

    /// <summary>
    /// Everything the scheduler does for one villager. Called by: Tick.
    /// </summary>
    private static void CheckVillager(Villager villager, float now)
    {
        if (VoyageGuard.IsExempt(villager)) return;

        var workstation = villager.GetWorkstation();
        var runner = villager.GetQuestRunner();
        if (runner == null) return;

        if (Plugin.AddCleanupToStationsWithout.Value) EnsureCleanupQuest(villager, workstation, runner);
        if (!Plugin.KeepCleaningUntilDone.Value || workstation == null) return;
        if (IsCleanupActiveOrRequested(runner)) return;

        var unneeded = FindUnneeded(villager, workstation);
        var key = villager.Pointer;
        if (!BackoffByVillager.TryGetValue(key, out var backoff))
        {
            backoff = new Backoff { DelaySeconds = Plugin.RecheckIntervalSeconds.Value };
            BackoffByVillager[key] = backoff;
        }

        if (unneeded.Count == 0)
        {
            // Clean: forget any back-off, so a later job change is acted on promptly.
            backoff.CountAtLastRequest = -1;
            backoff.DelaySeconds = Plugin.RecheckIntervalSeconds.Value;
            return;
        }
        if (now < backoff.NextAllowedAt) return;

        // No progress since the last request? Wait longer before the next one.
        var madeProgress = backoff.CountAtLastRequest < 0 || unneeded.Count < backoff.CountAtLastRequest;
        backoff.DelaySeconds = madeProgress
            ? Plugin.RecheckIntervalSeconds.Value
            : Mathf.Min(backoff.DelaySeconds * 2f, MaxBackoffSeconds);
        backoff.NextAllowedAt = now + backoff.DelaySeconds;
        backoff.CountAtLastRequest = unneeded.Count;

        DiagnosticLog.Write("unneeded_items_found",
            $"{GameDescribe.Villager(villager)} count={unneeded.Count} items={DiagnosticLog.Quote(string.Join(", ", unneeded))} " +
            $"progress={madeProgress} next_check_seconds={backoff.DelaySeconds:0}");
        ManualCleanup.RequestCleanup(villager, important: false, reason: "unneeded_items");
    }

    /// <summary>
    /// True when a cleanup quest is running or already requested, so the
    /// scheduler does not stack requests. Called by: CheckVillager.
    /// </summary>
    private static bool IsCleanupActiveOrRequested(QuestRunner runner)
    {
        if (runner.GetActiveQuest()?.TryCast<CleanupInventoryQuest>() != null) return true;
        foreach (var entry in runner._questDataTable)
        {
            var data = entry.Value?.TryCast<CleanupInventoryQuest.CleanupInventoryQuestData>();
            if (data != null && data.CleanupRequested) return true;
        }
        return false;
    }

    /// <summary>
    /// Lists the eligible items the villager's job does not need, using the
    /// game's own tests. Mirrors the rules in CleanupBehaviourPatches: equipped
    /// items only count if they are tools (and the villager is not a warrior).
    /// Called by: CheckVillager.
    /// </summary>
    /// <returns>Names of the unneeded items (empty when clean).</returns>
    private static List<string> FindUnneeded(Villager villager, Workstation workstation)
    {
        var result = new List<string>();
        var items = villager.GetInventory()?.GetAllItems();
        if (items == null) return result;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var info = item?.info;
            if (info == null || !ItemEligibility.IsEligible(item)) continue;

            var equipment = item.TryCast<EquipmentItem>();
            if (equipment != null && equipment.IsEquipped())
            {
                var cleanable = Plugin.CleanEquippedTools.Value && ItemEligibility.IsTool(item) && !villager.IsWarrior;
                if (!cleanable) continue;
            }

            if (workstation.IsItemNeededByVillager(info, villager)) continue;
            if (workstation.IsFuelNeededByVillager(info, villager)) continue;
            result.Add(info.Name);
        }
        return result;
    }

    /// <summary>
    /// Gives the villager a cleanup quest when their station has none, and
    /// removes one this mod added when it no longer applies.
    /// Called by: CheckVillager.
    /// </summary>
    private static void EnsureCleanupQuest(Villager villager, Workstation workstation, QuestRunner runner)
    {
        var key = villager.Pointer;
        InjectedByVillager.TryGetValue(key, out var injected);

        // Does the villager have a cleanup quest other than ours?
        var hasOwn = false;
        foreach (var entry in runner._questDataTable)
        {
            var quest = entry.Key?.TryCast<CleanupInventoryQuest>();
            if (quest == null) continue;
            if (injected != null && quest.Pointer == injected.Quest.Pointer) continue;
            hasOwn = true;
        }

        // Remove ours if the villager changed job, lost it, or now has their own.
        if (injected != null && (workstation == null || hasOwn || injected.Workstation != workstation.Pointer))
        {
            runner.RemoveQuest(injected.Quest);
            InjectedByVillager.Remove(key);
            DiagnosticLog.Write("cleanup_quest_removed", $"{GameDescribe.Villager(villager)} has_own={hasOwn}");
            injected = null;
        }

        if (workstation == null || hasOwn || injected != null) return;

        var questForStation = GetOrCreateInjectedQuest(workstation);
        if (questForStation == null) return;
        runner.AddQuest(questForStation);
        InjectedByVillager[key] = new Injected { Quest = questForStation, Workstation = workstation.Pointer };
        DiagnosticLog.Write("cleanup_quest_added",
            $"{GameDescribe.Villager(villager)} station={DiagnosticLog.Quote(workstation.name)} " +
            $"station_type={workstation.GetIl2CppType().FullName}");
    }

    /// <summary>
    /// Returns the shared injected cleanup quest for a station, creating it the
    /// way CraftingStation.Awake creates its own:
    /// new CleanupInventoryQuest(station, cleanupInventoryBehaviour).
    /// Returns null until a CraftingStation exists to borrow the behaviour from.
    /// </summary>
    private static CleanupInventoryQuest GetOrCreateInjectedQuest(Workstation workstation)
    {
        var key = workstation.Pointer;
        if (InjectedQuestByStation.TryGetValue(key, out var existing) && existing != null) return existing;

        var behaviour = FindCleanupBehaviour();
        if (behaviour == null) return null;

        var quest = new CleanupInventoryQuest(workstation, behaviour);
        InjectedQuestByStation[key] = quest;
        return quest;
    }

    /// <summary>
    /// Finds the game's authored cleanup FSM behaviour, from any CraftingStation.
    /// Cached once found.
    /// </summary>
    private static vFSMBehaviour FindCleanupBehaviour()
    {
        if (_cleanupBehaviour != null) return _cleanupBehaviour;
        foreach (var station in UnityEngine.Object.FindObjectsOfType<CraftingStation>())
        {
            var behaviour = station.cleanupInventoryBehaviour;
            if (behaviour == null) continue;
            _cleanupBehaviour = behaviour;
            DiagnosticLog.Write("cleanup_behaviour_found", $"source={DiagnosticLog.Quote(station.name)} behaviour={DiagnosticLog.Quote(behaviour.name)}");
            break;
        }
        return _cleanupBehaviour;
    }

    /// <summary>
    /// Sets maxDistanceToStorage on every cleanup FSM asset to the configured
    /// distance (when above 0), logging the authored value the first time.
    /// Called by: StartSweep.
    /// </summary>
    private static void ApplyStorageSearchDistance()
    {
        var distance = Plugin.StorageSearchDistance.Value;
        if (distance <= 0f) return;

        foreach (var fsm in Resources.FindObjectsOfTypeAll<FSM_CleanupInventory>())
        {
            if (fsm == null) continue;
            if (Mathf.Approximately(fsm.maxDistanceToStorage, distance)) continue;

            if (DistanceApplied.Add(fsm.Pointer))
                DiagnosticLog.Write("storage_search_distance_set",
                    $"fsm={DiagnosticLog.Quote(fsm.name)} authored={fsm.maxDistanceToStorage:0.#} now={distance:0.#}");
            fsm.maxDistanceToStorage = distance;
        }
    }
}

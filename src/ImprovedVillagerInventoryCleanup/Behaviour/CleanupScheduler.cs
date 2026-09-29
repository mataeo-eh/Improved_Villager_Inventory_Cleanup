// CleanupScheduler - makes sure cleanup actually happens, and keeps happening
// until a villager carries nothing their job does not need.
//
// Role in the larger system
// -------------------------
// The patches make unneeded items *eligible*; this class makes sure the
// cleanup quest *runs*. Vanilla only requests a cleanup on a job, schedule or
// viking-status change, and forgets the request when a run ends or is
// interrupted. In the 0.4.0 test that meant pressing the button several times
// to clear all of a villager's tools. Three jobs:
//
// 1. Keep cleaning (KeepCleaningUntilDone). A completed pass that removed an
//    eligible item re-arms its quest before the runner ranks the next task.
//    No recursive Start/Stop or ReevaluateQuest inside a Stop callback: the
//    native QuestRunner.FSMQuestStop performs that notification itself after
//    clearing the active quest and stopping its data. Periodically, each
//    villager's inventory is checked with the game's own test,
//    Workstation.IsItemNeededByVillager (and IsFuelNeededByVillager). If
//    anything eligible is not needed, the villager's cleanup is requested with
//    the ordinary (not "important") priority, so it slots in around work the
//    way a vanilla job-change cleanup does. After a pass removes nothing, the
//    scheduler stops requesting that villager's cleanup. A later game or manual
//    request can still start it; a productive pass re-enables continuation.
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
    private sealed class UnneededInventory
    {
        internal readonly List<string> Names = new();
        internal readonly Dictionary<IntPtr, int> Quantities = new();
        internal int Count => Quantities.Count;
    }

    private sealed class CleanupPass
    {
        internal Villager Villager;
        internal IntPtr Workstation;
        internal UnneededInventory Before;
        internal bool Important;
    }

    /// <summary>A cleanup quest this mod added to one villager.</summary>
    private sealed class Injected
    {
        internal CleanupInventoryQuest Quest;
        internal IntPtr Workstation;
    }

    // Villagers still to check in the current sweep; a few are checked per
    // frame so a sweep never causes a hitch.
    private const int VillagersPerFrame = 3;
    private static readonly Queue<Villager> Pending = new();
    private static float _nextSweepAt;

    private static readonly Dictionary<IntPtr, CleanupFlowState> FlowByVillager = new();
    private static readonly Dictionary<IntPtr, CleanupPass> PassByData = new();
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
                if (villager != null) CheckVillager(villager);
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
            StorageRules.RebuildIndex();
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
    private static void CheckVillager(Villager villager)
    {
        if (VoyageGuard.IsExempt(villager)) return;

        var workstation = villager.GetWorkstation();
        var runner = villager.GetQuestRunner();
        if (runner == null) return;

        if (Plugin.AddCleanupToStationsWithout.Value) EnsureCleanupQuest(villager, workstation, runner);
        if (!Plugin.KeepCleaningUntilDone.Value || workstation == null) return;
        if (FlowByVillager.TryGetValue(villager.Pointer, out var flow) && !flow.AllowAutomaticRequest) return;
        if (IsCleanupActiveOrRequested(runner)) return;

        var unneeded = FindUnneeded(villager, workstation);
        if (unneeded.Count == 0) return;

        DiagnosticLog.Write("unneeded_items_found",
            $"{GameDescribe.Villager(villager)} count={unneeded.Count} items={DiagnosticLog.Quote(string.Join(", ", unneeded.Names))} " +
            "automatic_request=true");
        ManualCleanup.RequestCleanup(villager, important: false, reason: "unneeded_items");
    }

    /// <summary>Records what this pass can remove, including partial stacks and its original priority.</summary>
    internal static void OnCleanupStarted(CleanupInventoryQuest.CleanupInventoryQuestData data)
    {
        if (data == null) return;
        PassByData.Remove(data.Pointer);
        if (!Plugin.EnableImprovedCleanup.Value || !Plugin.KeepCleaningUntilDone.Value) return;

        try
        {
            var villager = data.GetVillager();
            if (villager == null || VoyageGuard.IsExempt(villager)) return;
            var workstation = villager.GetWorkstation();
            if (workstation == null) return;

            var before = FindUnneeded(villager, workstation);
            PassByData[data.Pointer] = new CleanupPass
            {
                Villager = villager, Workstation = workstation.Pointer,
                Before = before, Important = data.Important
            };
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("cleanup_pass_start_failed", exception);
        }
    }

    /// <summary>
    /// Re-arms a completed pass before FSMQuestStop's native quest notification.
    /// Urgent needs still win normal quest ranking. Interruptions, changed jobs,
    /// voyages, and unsuccessful passes do not immediately restart cleanup.
    /// </summary>
    internal static void OnCleanupStopped(CleanupInventoryQuest.CleanupInventoryQuestData data)
    {
        if (data == null || !PassByData.Remove(data.Pointer, out var pass)) return;
        if (!Plugin.EnableImprovedCleanup.Value || !Plugin.KeepCleaningUntilDone.Value) return;

        try
        {
            var villager = pass.Villager;
            if (villager == null || VoyageGuard.IsExempt(villager)) return;
            var workstation = villager.GetWorkstation();
            if (workstation == null || workstation.Pointer != pass.Workstation) return;
            var after = FindUnneeded(villager, workstation);
            var madeProgress = CleanupProgress.MadeProgress(pass.Before.Quantities, after.Quantities);
            var completed = data.questStatus == QuestStatus.Completed;
            if (!FlowByVillager.TryGetValue(villager.Pointer, out var flow))
                FlowByVillager[villager.Pointer] = flow = new CleanupFlowState();
            flow.FinishedPass(madeProgress);

            var runner = data.QuestRunner;
            var quest = data.Quest;
            var registered = runner != null && quest != null && runner.GetQuestData(quest)?.Pointer == data.Pointer;
            var continueNow = completed && madeProgress && after.Count > 0 && registered;
            if (continueNow)
            {
                data.CleanupRequested = true;
                data.Important = pass.Important;
            }
            DiagnosticLog.Write("cleanup_pass_finished",
                $"{GameDescribe.Villager(villager)} status={data.questStatus} remaining={after.Count} " +
                $"progress={madeProgress} continue_now={continueNow} important={pass.Important} " +
                $"automatic_requests_allowed={flow.AllowAutomaticRequest}");
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("cleanup_pass_stop_failed", exception);
        }
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
    /// <returns>Names and per-item quantities of unneeded inventory (empty when clean).</returns>
    private static UnneededInventory FindUnneeded(Villager villager, Workstation workstation)
    {
        var result = new UnneededInventory();
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
            result.Names.Add(info.Name);
            result.Quantities[item.Pointer] = Math.Max(1, item.count);
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

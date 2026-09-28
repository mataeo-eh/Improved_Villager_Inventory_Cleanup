// VoyageGuard - keeps the mod's hands off villagers who are away on a karvi
// voyage.
//
// Role in the larger system: every behavioural patch and the scheduler call
// IsExempt(villager) first. An exempt villager gets pure vanilla behaviour: no
// extra items considered, no storage overrides, no last-resort redirect, no
// cleanup requests. A crew at sea must not start dropping gear or try to walk
// back to the Eye of Odin.
//
// "Away" is read from the game's own expedition state: the villager's
// SailingShip (Villager.GetSailingShip) and its ExpeditionNetworkState.Stage.
// The stages are None, Supply, Embark, Sailing, Deployed, Unloading. Only
// Sailing (at sea) and Deployed (landed away on the trip) count as away; a crew
// still supplying or boarding at home, or unloading after the return, is
// treated normally.
//
// The answer is cached per villager for a second, because CheckItem asks it
// for every item of every scan.
//
// Depends on: DiagnosticLog, GameDescribe.

using System;
using System.Collections.Generic;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame;
using SSSGame.Water;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class VoyageGuard
{
    private sealed class Cached
    {
        internal bool Away;
        internal float CheckedAt;
    }

    private const float CacheSeconds = 1f;
    private static readonly Dictionary<IntPtr, Cached> ByVillager = new();

    /// <summary>
    /// True when the villager is away on a voyage and must be left fully vanilla.
    /// Never throws; if the state cannot be read the villager is not exempt.
    /// Called by: every behavioural patch, CleanupWatcher, CleanupScheduler, ManualCleanup.
    /// </summary>
    /// <param name="villager">The villager to check.</param>
    internal static bool IsExempt(Villager villager)
    {
        if (villager == null) return false;
        var key = villager.Pointer;
        var now = Time.realtimeSinceStartup;
        if (ByVillager.TryGetValue(key, out var cached) && now - cached.CheckedAt < CacheSeconds)
            return cached.Away;

        var away = ReadAway(villager, out var stage);
        if (cached == null)
        {
            cached = new Cached();
            ByVillager[key] = cached;
        }
        else if (cached.Away != away)
        {
            // Log each change, so a test run shows crews leaving and returning.
            DiagnosticLog.Write("voyage_state_changed",
                $"{GameDescribe.Villager(villager)} away={away} stage={stage}");
        }
        cached.Away = away;
        cached.CheckedAt = now;
        return away;
    }

    /// <summary>Reads the villager's ship stage. Called by IsExempt.</summary>
    private static bool ReadAway(Villager villager, out string stageText)
    {
        stageText = "none";
        try
        {
            var ship = villager.GetSailingShip();
            if (ship == null) return false;
            var state = ship.netState;
            if (state == null) return false;

            var stage = state.Stage;
            stageText = stage.ToString();
            return stage == ExpeditionStage.Sailing || stage == ExpeditionStage.Deployed;
        }
        catch
        {
            return false;
        }
    }
}

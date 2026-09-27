using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.AI;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Diagnostics;

internal static class DiagnosticTracker
{
    private sealed class Observation
    {
        internal Villager Villager;
        internal float ExpiresAt;
        internal float NextSnapshotAt;
    }

    private static readonly Dictionary<int, Observation> Observations = new();

    /// <summary>
    /// Returns the key used to identify a villager in the observation table.
    /// </summary>
    /// <remarks>
    /// This deliberately does NOT use <c>GetInstanceID()</c>. In the 0.1.0 log,
    /// 64 of 129 distinct (instance, persistent) pairs shared an instance ID with
    /// a different villager - Il2Cpp reuses the managed wrapper, so the instance
    /// ID is not a stable per-villager identity. Keying the table on it made
    /// roughly half of all tracking attributions land on the wrong villager.
    /// <c>PersistentUniqueID</c> is the settlement-stable identifier and is what
    /// the analysis of that log had to be filtered on to be trustworthy.
    /// Called by: <see cref="Track"/>, <see cref="IsTracked"/>.
    /// </remarks>
    /// <param name="villager">The villager to identify.</param>
    /// <returns>A stable key, or 0 if the identifier cannot be read.</returns>
    private static int KeyFor(Villager villager)
    {
        try { return villager.PersistentUniqueID; }
        catch { return 0; }
    }

    /// <summary>
    /// Puts a villager under detailed observation, or extends an observation that
    /// is already running.
    /// </summary>
    /// <remarks>
    /// An existing observation is never shortened. A villager who was reassigned
    /// and is therefore being watched for the full window may also start a cleanup
    /// quest, and that shorter request must not cut the longer watch short.
    /// Re-tracking an already-watched villager is also kept quiet in the log,
    /// because cleanup quests can start repeatedly and would otherwise bury the
    /// interesting events under duplicate `tracking_started` lines.
    /// </remarks>
    /// <param name="villager">The villager to observe.</param>
    /// <param name="reason">Short tag recording what caused tracking to begin.</param>
    /// <param name="windowSeconds">
    /// How long to observe for. Pass a negative value to use the configured
    /// default (<c>TrackingWindowSeconds</c>).
    /// </param>
    internal static void Track(Villager villager, string reason, float windowSeconds = -1f)
    {
        if (villager == null) return;
        try
        {
            var now = Time.realtimeSinceStartup;
            var key = KeyFor(villager);
            if (key == 0) return;

            var requested = windowSeconds < 0f ? Plugin.TrackingWindowSeconds.Value : windowSeconds;
            var expiresAt = now + Math.Max(10f, requested);

            if (Observations.TryGetValue(key, out var existing))
            {
                // Already watched: keep the later deadline and say nothing further.
                if (expiresAt > existing.ExpiresAt) existing.ExpiresAt = expiresAt;
                existing.Villager = villager;
                return;
            }

            Observations[key] = new Observation
            {
                Villager = villager,
                ExpiresAt = expiresAt,
                NextSnapshotAt = now
            };
            DiagnosticLog.Write("tracking_started",
                $"{GameDescribe.Villager(villager)} reason={reason} window={Math.Max(10f, requested):0.###} " +
                $"inventory={GameDescribe.Inventory(villager)}");
        }
        catch (Exception exception) { DiagnosticLog.WriteException("tracking_start_failed", exception); }
    }

    internal static bool IsTracked(Villager villager)
    {
        if (villager == null) return false;
        try
        {
            var key = KeyFor(villager);
            return key != 0 && Observations.ContainsKey(key);
        }
        catch { return false; }
    }

    internal static bool TryGetVillager(QuestRunner runner, out Villager villager)
    {
        villager = null;
        if (runner == null) return false;
        foreach (var observation in Observations.Values)
        {
            try
            {
                if (observation.Villager?.GetQuestRunner()?.Pointer == runner.Pointer)
                {
                    villager = observation.Villager;
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    internal static void SnapshotAll()
    {
        var now = Time.realtimeSinceStartup;
        var expired = new List<int>();
        foreach (var pair in Observations)
        {
            var observation = pair.Value;
            if (observation.Villager == null || now >= observation.ExpiresAt)
            {
                expired.Add(pair.Key);
                continue;
            }
            if (now < observation.NextSnapshotAt) continue;

            observation.NextSnapshotAt = now + Math.Max(1f, Plugin.SnapshotIntervalSeconds.Value);
            try
            {
                DiagnosticLog.Write("periodic_snapshot",
                    $"{GameDescribe.Villager(observation.Villager)} workstation={GameDescribe.Workstation(observation.Villager.GetWorkstation())} " +
                    $"active_quest={GameDescribe.Quest(observation.Villager.GetQuestRunner())} inventory={GameDescribe.Inventory(observation.Villager)}");
            }
            catch (Exception exception) { DiagnosticLog.WriteException("periodic_snapshot_failed", exception); }
        }
        foreach (var key in expired) Observations.Remove(key);
    }
}

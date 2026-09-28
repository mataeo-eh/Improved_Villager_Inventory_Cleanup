// DropPoint - where a villager drops an item as a last resort, when no
// storage anywhere will take it.
//
// Role in the larger system: CleanupWatcher asks Get(villager) when a villager
// is on the game's ground-drop path, and points the villager's walk there.
//
// - Villagers who live at an outpost drop in front of their outpost's
//   OutpostStructure (matched by Villager.GetHomeOutpostId against
//   Settlement._outposts).
// - Everyone else drops in front of the Eye of Odin, the settlement core
//   (Settlement._settlementCore; the 0.4.0 test confirmed it is
//   "HearthstoneCoreTier3").
//
// Each destination gets one hidden marker object, placed StandOffDistance
// metres in front of the structure, so villagers aim for walkable ground rather
// than the structure's centre. One marker per destination means villagers of
// different outposts never pull each other's walk target around.
//
// Depends on: Plugin (config), DiagnosticLog, GameDescribe.

using System;
using System.Collections.Generic;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SSSGame;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class DropPoint
{
    // destination structure pointer -> marker.
    private static readonly Dictionary<IntPtr, GameObject> MarkerByStructure = new();

    // Destinations already logged, to log each once.
    private static readonly HashSet<IntPtr> Logged = new();

    /// <summary>
    /// Returns the marker transform the villager should walk to, or null when
    /// neither an outpost nor the Eye of Odin can be found.
    /// Called by: CleanupWatcher.TryRedirect.
    /// </summary>
    /// <param name="villager">The villager about to drop an item.</param>
    internal static Transform Get(Villager villager)
    {
        var settlement = villager?.GetSettlement();
        if (settlement == null) return null;

        var outpost = FindHomeOutpost(villager, settlement);
        if (outpost != null) return MarkerFor(outpost, "outpost");

        var core = settlement._settlementCore;
        return core == null ? null : MarkerFor(core, "eye_of_odin");
    }

    /// <summary>
    /// Finds the outpost the villager lives at, or null for villagers of the
    /// main settlement (or when the outpost cannot be found).
    /// </summary>
    private static Structure FindHomeOutpost(Villager villager, Settlement settlement)
    {
        try
        {
            var outpostId = villager.GetHomeOutpostId();
            if (outpostId == Settlement.c_DefaultOutpostId) return null;

            var outposts = settlement._outposts;
            if (outposts == null) return null;
            for (var index = 0; index < outposts.Count; index++)
            {
                var outpost = outposts[index];
                if (outpost != null && outpost.OutpostId == outpostId) return outpost;
            }
        }
        catch (Exception exception)
        {
            DiagnosticLog.WriteException("outpost_lookup_failed", exception);
        }
        return null;
    }

    /// <summary>
    /// Creates or moves the marker standing in front of <paramref name="structure"/>.
    /// </summary>
    /// <param name="structure">The Eye of Odin or an outpost.</param>
    /// <param name="kind">"eye_of_odin" or "outpost", for the log.</param>
    private static Transform MarkerFor(Structure structure, string kind)
    {
        var structureTransform = structure.transform;
        var standPoint = structureTransform.position +
                         structureTransform.forward * Plugin.EyeOfOdinStandOffDistance.Value;

        var key = structure.Pointer;
        if (!MarkerByStructure.TryGetValue(key, out var marker) || marker == null)
        {
            marker = new GameObject($"ImprovedVillagerInventoryCleanup.DropPoint.{kind}");
            UnityEngine.Object.DontDestroyOnLoad(marker);
            marker.hideFlags = HideFlags.HideAndDontSave;
            MarkerByStructure[key] = marker;
        }
        marker.transform.position = standPoint;

        if (Logged.Add(key))
            DiagnosticLog.Write("drop_point_resolved",
                $"kind={kind} structure={DiagnosticLog.Quote(structure.name)} " +
                $"structure_position={structureTransform.position} stand_point={standPoint}");
        return marker.transform;
    }
}

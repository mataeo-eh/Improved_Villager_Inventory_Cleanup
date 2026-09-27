// ItemEligibility - decides which items this mod lets the cleanup FSM consider
// beyond what vanilla already allows.
//
// Role in the larger system: CleanupBehaviourPatches asks IsEligible(item) for
// every item the cleanup scan looks at. "Eligible" only means "allowed to be
// ranked"; vanilla's own ranking still decides whether the item is actually
// removed (see CleanupBehaviourPatches for why).
//
// Eligibility is by category path, e.g. "Tools/Axes", matched against the
// comma-separated prefixes in the EligibleCategories config entry. Category
// names come from the 0.2.0 log, which recorded every item's path.
//
// Depends on: Plugin (config), GameDescribe.CategoryPath.

using System;
using System.Collections.Generic;
using ImprovedVillagerInventoryCleanup.Diagnostics;
using SandSailorStudio.Inventory;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class ItemEligibility
{
    // item id -> eligible. Category paths never change at runtime, so each item
    // type is resolved once. Cleared when the config entry changes.
    private static readonly Dictionary<int, bool> CacheByItemId = new();
    private static string[] _prefixes;

    /// <summary>
    /// Re-reads the EligibleCategories config entry and drops cached answers.
    /// Called once from Plugin.Load and again whenever the entry is edited.
    /// </summary>
    internal static void Reload()
    {
        var prefixes = new List<string>();
        foreach (var part in (Plugin.EligibleCategories.Value ?? "").Split(','))
        {
            var trimmed = part.Trim().Trim('/');
            if (trimmed.Length > 0) prefixes.Add(trimmed);
        }
        _prefixes = prefixes.ToArray();
        CacheByItemId.Clear();
    }

    /// <summary>
    /// Returns true when the item's category path starts with one of the
    /// configured prefixes. "Tools" matches "Tools/Axes" but not "Toolsets".
    /// Never throws; an unreadable item is treated as not eligible.
    /// Called by: CleanupCheckItemBehaviourPatch.Prefix.
    /// </summary>
    /// <param name="item">The inventory item being scanned.</param>
    /// <returns>True if the mod should let the cleanup scan rank this item.</returns>
    internal static bool IsEligible(Item item)
    {
        try
        {
            var info = item?.info;
            if (info == null) return false;
            if (CacheByItemId.TryGetValue(info.id, out var cached)) return cached;

            var path = GameDescribe.CategoryPath(info);
            var eligible = false;
            foreach (var prefix in _prefixes ?? Array.Empty<string>())
            {
                var exact = path.Equals(prefix, StringComparison.OrdinalIgnoreCase);
                var underIt = path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
                if (exact || underIt) { eligible = true; break; }
            }

            CacheByItemId[info.id] = eligible;
            return eligible;
        }
        catch
        {
            return false;
        }
    }
}

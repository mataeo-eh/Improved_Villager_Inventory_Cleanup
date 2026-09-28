// ItemEligibility - decides which items this mod lets the cleanup FSM consider
// beyond what vanilla already allows.
//
// Role in the larger system: CleanupBehaviourPatches asks IsEligible(item) for
// every item a cleanup scan looks at, and CleanupScheduler asks it when
// deciding whether a villager still carries anything unneeded. "Eligible" only
// means "allowed to be ranked"; the game's own ranking still keeps anything the
// villager's job needs (see CleanupBehaviourPatches).
//
// Categories are matched by path, e.g. "Resources/Stone". An item is eligible
// when its path is under an entry of EligibleItemCategories ("*" = all) and not
// under an entry of ExcludedItemCategories.
//
// Category names are compared in a normalised form, because the game sometimes
// hands back untranslated keys: the 0.3.0 log shows both "Tools/Axes" and
// "category.Tools_name/category.Tools_Axes_name" for the same item. 0.4.0 only
// understood the first form and cached "not eligible" for the second, which is
// one reason some tools were skipped. Both now normalise to "tools/axes".
//
// Depends on: Plugin (config).

using System;
using System.Collections.Generic;
using System.Text;
using SandSailorStudio.Inventory;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class ItemEligibility
{
    // item id -> eligible. Category paths never change at runtime, so each item
    // type is resolved once. Cleared when either config list changes.
    private static readonly Dictionary<int, bool> CacheByItemId = new();

    private static bool _allEligible;
    private static string[] _eligible = Array.Empty<string>();
    private static string[] _excluded = Array.Empty<string>();

    /// <summary>
    /// Re-reads both category lists and drops cached answers.
    /// Called from Plugin.BindConfig and whenever either list is edited.
    /// </summary>
    internal static void Reload()
    {
        _eligible = ParseList(Plugin.EligibleCategories.Value, out _allEligible);
        _excluded = ParseList(Plugin.ExcludedCategories.Value, out _);
        CacheByItemId.Clear();
    }

    /// <summary>
    /// True when the item may be put away by this mod (subject to the game's
    /// "needed at work" ranking). Never throws; unreadable items are not eligible.
    /// Called by: CleanupCheckItemBehaviourPatch.Prefix, CleanupScheduler.
    /// </summary>
    /// <param name="item">The inventory item.</param>
    internal static bool IsEligible(Item item)
    {
        try
        {
            var info = item?.info;
            if (info == null) return false;
            if (CacheByItemId.TryGetValue(info.id, out var cached)) return cached;

            var path = NormalisedPath(info);
            var eligible = (_allEligible || MatchesAny(path, _eligible)) && !MatchesAny(path, _excluded);
            CacheByItemId[info.id] = eligible;
            return eligible;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// True when the item's category is under "Tools". Used for the
    /// equipped-tool rule. Called by: CleanupBehaviourPatches, CleanupScheduler.
    /// </summary>
    /// <param name="item">The inventory item.</param>
    internal static bool IsTool(Item item)
    {
        try
        {
            var info = item?.info;
            return info != null && MatchesAny(NormalisedPath(info), new[] { "tools" });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the item's category path, root first, in normalised form
    /// (see NormaliseSegment). The depth cap guards against the cyclic parent
    /// chains the game has for some blueprint categories.
    /// </summary>
    /// <param name="info">The item definition.</param>
    /// <returns>For example "resources/stone", or "" when uncategorised.</returns>
    private static string NormalisedPath(ItemInfo info)
    {
        var names = new List<string>(8);
        var category = info.category;
        for (var depth = 0; category != null && depth < 8; depth++)
        {
            names.Add(NormaliseSegment(category.Name));
            category = category.parent;
        }
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>
    /// Normalises one category name so translated and untranslated forms match:
    /// "category.Tools_Road Makers_name" and "Roadmakers" both become
    /// "roadmakers". Lower-case, spaces removed, and for the untranslated form
    /// the "category." prefix, "_name" suffix and parent prefix ("Tools_") removed.
    /// </summary>
    /// <param name="name">A category name as the game reports it.</param>
    private static string NormaliseSegment(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var text = name;
        if (text.StartsWith("category.", StringComparison.Ordinal) && text.EndsWith("_name", StringComparison.Ordinal))
        {
            text = text.Substring(9, text.Length - 9 - 5);
            var underscore = text.IndexOf('_');
            if (underscore >= 0) text = text.Substring(underscore + 1);
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
            if (!char.IsWhiteSpace(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    /// <summary>
    /// Splits a comma-separated config list into normalised paths. "*" sets
    /// <paramref name="containsWildcard"/> instead of being added.
    /// </summary>
    private static string[] ParseList(string value, out bool containsWildcard)
    {
        containsWildcard = false;
        var result = new List<string>();
        foreach (var part in (value ?? "").Split(','))
        {
            var trimmed = part.Trim().Trim('/');
            if (trimmed.Length == 0) continue;
            if (trimmed == "*") { containsWildcard = true; continue; }

            var segments = trimmed.Split('/');
            for (var index = 0; index < segments.Length; index++)
                segments[index] = NormaliseSegment(segments[index]);
            result.Add(string.Join("/", segments));
        }
        return result.ToArray();
    }

    /// <summary>
    /// True when <paramref name="path"/> equals, or is under, any entry.
    /// "tools" matches "tools/axes" but not "toolsets".
    /// </summary>
    private static bool MatchesAny(string path, string[] entries)
    {
        foreach (var entry in entries)
        {
            if (path == entry) return true;
            if (path.StartsWith(entry + "/", StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

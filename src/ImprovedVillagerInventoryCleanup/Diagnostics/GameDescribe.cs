using System;
using System.Collections.Generic;
using System.Reflection;
using SandSailorStudio.Inventory;
using SSSGame;
using SSSGame.AI;

namespace ImprovedVillagerInventoryCleanup.Diagnostics;

internal static class GameDescribe
{
    internal static string Villager(Villager villager)
    {
        if (villager == null) return "<null>";
        try
        {
            return $"name={DiagnosticLog.Quote(villager.GetName())} instance={villager.GetInstanceID()} persistent={villager.PersistentUniqueID}";
        }
        catch (Exception exception)
        {
            return $"instance={SafeInstanceId(villager)} describe_error={DiagnosticLog.Quote(exception.Message)}";
        }
    }

    internal static string Workstation(object workstation)
    {
        if (workstation == null) return "<none>";
        try { return $"type={workstation.GetType().FullName} value={DiagnosticLog.Quote(workstation.ToString())}"; }
        catch { return "<unreadable>"; }
    }

    internal static string Item(Item item)
    {
        if (item == null) return "<null>";
        try
        {
            var info = item.info;
            if (info == null) return $"name=<null> id=<null> count={item.count}";

            // Category and tier are what the roadmap's "drop it if a higher tier
            // exists" rules will key off. We record them now, while the mod is
            // still mostly observing, so the next log tells us the real taxonomy
            // instead of us guessing category names from item display names.
            return $"name={DiagnosticLog.Quote(info.Name)} id={info.id} count={item.count} " +
                   $"category={DiagnosticLog.Quote(CategoryPath(info))} tier={SafeTier(info)}";
        }
        catch (Exception exception)
        {
            return $"describe_error={DiagnosticLog.Quote(exception.Message)}";
        }
    }

    /// <summary>
    /// Builds a slash-separated path for an item's category, from the root
    /// category down to the item's own (for example "Tools/Axes").
    /// ASKA nests categories through <c>ItemCategoryInfo.parent</c>, and the full
    /// path is far more useful for writing filters than the leaf name alone.
    /// Returns "&lt;none&gt;" when the item has no category assigned.
    /// Called by: <see cref="Item"/>.
    /// </summary>
    /// <param name="info">The item definition to read the category from.</param>
    /// <returns>Root-to-leaf category path, or a placeholder when unavailable.</returns>
    internal static string CategoryPath(ItemInfo info)
    {
        try
        {
            var category = info.category;
            if (category == null) return "<none>";

            // Collect leaf -> root then reverse. The depth cap means a corrupt or
            // cyclic parent chain can never hang the game thread we are running on.
            var names = new List<string>(8);
            for (var depth = 0; category != null && depth < 8; depth++)
            {
                names.Add(category.Name ?? "<unnamed>");
                category = category.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
        catch { return "<unreadable>"; }
    }

    /// <summary>
    /// Reads <c>ItemInfo.tier</c> defensively. Tier is only meaningful when the
    /// item sets <c>showTier</c>, so that flag is reported alongside the number
    /// rather than being silently dropped.
    /// Called by: <see cref="Item"/>.
    /// </summary>
    /// <param name="info">The item definition to read the tier from.</param>
    /// <returns>Text in the form "2(shown=True)", or a placeholder on failure.</returns>
    private static string SafeTier(ItemInfo info)
    {
        try { return $"{info.tier}(shown={info.showTier})"; }
        catch { return "<unreadable>"; }
    }

    internal static string Inventory(Villager villager)
    {
        if (villager == null) return "<null>";
        try
        {
            var items = villager.GetInventory()?.GetAllItems();
            if (items == null || items.Count == 0) return "[]";

            var values = new List<string>(items.Count);
            for (var index = 0; index < items.Count; index++)
                values.Add("{" + Item(items[index]) + "}");
            return "[" + string.Join(",", values) + "]";
        }
        catch (Exception exception)
        {
            return $"<inventory_error:{DiagnosticLog.Quote(exception.Message)}>";
        }
    }

    internal static string StorageDecision(StorageInteraction storage, Item item)
    {
        if (storage == null) return "storage=<null>";
        try
        {
            var info = item?.info;
            var check = info == null ? "<no_item_info>" : storage.Check(info).ToString();
            var container = storage.Container;
            return $"storage_check={check} can_store_type={InvokeMetric(container, "CanStoreItemType", info)} " +
                   $"has_space={InvokeMetric(container, "HasSpace", info)} remaining_capacity={InvokeMetric(container, "GetRemainingCapacity", info)} " +
                   $"existing_count={InvokeMetric(container, "GetItemCount", info)}";
        }
        catch (Exception exception)
        {
            return $"storage_detail_error={DiagnosticLog.Quote(exception.Message)}";
        }
    }

    internal static string Quest(QuestRunner runner)
    {
        if (runner == null) return "<none>";
        try
        {
            var quest = runner.GetActiveQuest();
            return quest == null ? "<none>" : $"type={quest.GetType().FullName} value={DiagnosticLog.Quote(quest.ToString())}";
        }
        catch (Exception exception)
        {
            return $"<quest_error:{DiagnosticLog.Quote(exception.Message)}>";
        }
    }

    private static int SafeInstanceId(Villager villager)
    {
        try { return villager.GetInstanceID(); }
        catch { return 0; }
    }

    private static string InvokeMetric(object target, string methodName, object itemInfo)
    {
        if (target == null) return "<no_container>";
        try
        {
            foreach (var method in target.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name != methodName) continue;
                var parameters = method.GetParameters();
                object result;
                if (parameters.Length == 0)
                    result = method.Invoke(target, null);
                else if (parameters.Length == 1 && itemInfo != null && parameters[0].ParameterType.IsInstanceOfType(itemInfo))
                    result = method.Invoke(target, new[] { itemInfo });
                else
                    continue;
                return result?.ToString() ?? "<null>";
            }
            return "<signature_unavailable>";
        }
        catch (Exception exception)
        {
            return $"<error:{exception.GetBaseException().Message}>";
        }
    }
}

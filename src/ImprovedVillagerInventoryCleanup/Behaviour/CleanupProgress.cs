// CleanupProgress - compares eligible inventory at the start and end of a pass.
// Kept independent of Unity/IL2CPP so partial-stack and no-progress cases can
// be checked without launching ASKA. Item identity matters: newly acquired
// items must not hide a successful deposit of an older item.

using System;
using System.Collections.Generic;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class CleanupProgress
{
    internal static bool MadeProgress(IReadOnlyDictionary<IntPtr, int> before, IReadOnlyDictionary<IntPtr, int> after)
    {
        foreach (var item in before)
            if (!after.TryGetValue(item.Key, out var remaining) || remaining < item.Value) return true;
        return false;
    }
}

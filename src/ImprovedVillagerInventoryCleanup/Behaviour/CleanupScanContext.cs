// CleanupScanContext - hand-off state between the patches that run inside one
// CleanupInventoryData.CheckItem call.
//
// Role in the larger system: Harmony gives each patch only its own method's
// arguments. The tool admission needs three patches to cooperate within a
// single CheckItem call: the CheckItem prefix arms it, the
// _CheckContainerNeedingSpace postfix (called from inside CheckItem) applies
// it, and the CheckItem postfix vets the result. These two flags carry that.
//
// Everything here is only touched on Unity's main thread, where all villager AI
// runs, so plain static fields are safe.
//
// Depends on: nothing.

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class CleanupScanContext
{
    /// <summary>
    /// Set by the CheckItem prefix when the current item should be let past
    /// vanilla's dropWithoutReason gate; read by the _CheckContainerNeedingSpace
    /// postfix. Only meaningful during one CheckItem call.
    /// </summary>
    internal static bool AdmissionPending;

    /// <summary>
    /// Set by the _CheckContainerNeedingSpace postfix when it actually changed
    /// the answer, so the CheckItem postfix knows the pick came from this mod.
    /// </summary>
    internal static bool AdmissionUsed;
}

// CleanupScanContext - shared state for one tick of the cleanup FSM.
//
// Role in the larger system: several Harmony patches need to cooperate within
// a single call of FSM_CleanupInventory.OnStateUpdate, but Harmony gives each
// patch only its own method's arguments. For example, the storage search
// (Settlement.FindStorageToDeposit) has no idea it was called by the cleanup
// FSM, or for which villager. This class carries that context between them.
//
// Everything here is only touched on Unity's main thread, where all villager AI
// runs, so plain static fields are safe.
//
// Lifecycle of one OnStateUpdate call:
//   BeginUpdate()        OnStateUpdate prefix
//   NoteScan(data)       every CheckItem during the inventory scan
//   NoteStorageSearch()  every FindStorageToDeposit after the scan
//   EndUpdate()          OnStateUpdate postfix
//
// Depends on: nothing but the game types.

using SSSGame.AI.FSM;

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal static class CleanupScanContext
{
    /// <summary>True while FSM_CleanupInventory.OnStateUpdate is running.</summary>
    internal static bool InUpdate { get; private set; }

    /// <summary>
    /// The cleanup data whose inventory was scanned during this update, or null
    /// when this update did not scan (it was walking, depositing, etc.).
    /// </summary>
    internal static FSM_CleanupInventory.CleanupInventoryData ScannedData { get; private set; }

    /// <summary>How many storage searches this update made.</summary>
    internal static int StorageSearches { get; private set; }

    /// <summary>True if any storage search this update found somewhere to deposit.</summary>
    internal static bool StorageFound { get; private set; }

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

    /// <summary>Starts a fresh context. Called by the OnStateUpdate prefix.</summary>
    internal static void BeginUpdate()
    {
        InUpdate = true;
        ScannedData = null;
        StorageSearches = 0;
        StorageFound = false;
    }

    /// <summary>
    /// Records which cleanup data is being scanned. Ignored outside an update,
    /// in case CheckItem is ever called from elsewhere.
    /// Called by: CleanupCheckItemBehaviourPatch.Prefix.
    /// </summary>
    /// <param name="data">The cleanup FSM state being scanned.</param>
    internal static void NoteScan(FSM_CleanupInventory.CleanupInventoryData data)
    {
        if (InUpdate) ScannedData = data;
    }

    /// <summary>
    /// Records a storage search made after a scan, and whether it succeeded.
    /// Called by: the FindStorageToDeposit postfix.
    /// </summary>
    /// <param name="found">True if the search returned a storage.</param>
    internal static void NoteStorageSearch(bool found)
    {
        StorageSearches++;
        if (found) StorageFound = true;
    }

    /// <summary>Ends the context. Called by the OnStateUpdate postfix.</summary>
    internal static void EndUpdate()
    {
        InUpdate = false;
        ScannedData = null;
        AdmissionPending = false;
        AdmissionUsed = false;
    }
}

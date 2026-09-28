// DiagnosticBehaviour - the mod's one Unity component, on a hidden object that
// survives scene loads. It gives the mod a per-frame tick and a GUI pass.
//
// Update: inventory snapshots of observed villagers, the CleanupWatcher
// (follows every cleanup decision, applies the last-resort redirect) and the
// CleanupScheduler (keeps cleanup running until villagers are done).
// OnGUI:  the "Clean inventory now" button in the villager menu.

using System;
using ImprovedVillagerInventoryCleanup.Behaviour;
using ImprovedVillagerInventoryCleanup.UI;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Diagnostics;

public sealed class DiagnosticBehaviour : MonoBehaviour
{
    public DiagnosticBehaviour(IntPtr pointer) : base(pointer) { }

    public void Update()
    {
        DiagnosticTracker.SnapshotAll();
        CleanupWatcher.Tick();
        CleanupScheduler.Tick();
    }

    public void OnGUI() => CleanupButton.Draw();
}

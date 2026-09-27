using System;
using UnityEngine;

namespace ImprovedVillagerInventoryCleanup.Diagnostics;

public sealed class DiagnosticBehaviour : MonoBehaviour
{
    public DiagnosticBehaviour(IntPtr pointer) : base(pointer) { }

    public void Update() => DiagnosticTracker.SnapshotAll();
}

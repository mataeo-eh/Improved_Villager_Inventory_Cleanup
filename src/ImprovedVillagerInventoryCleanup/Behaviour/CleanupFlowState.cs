// CleanupFlowState - decides whether the mod may request cleanup on a timer.
// Once a pass removes nothing, only the game or player may start the next pass.
// A productive later pass restores prompt continuation and future timer checks.

namespace ImprovedVillagerInventoryCleanup.Behaviour;

internal sealed class CleanupFlowState
{
    internal bool AllowAutomaticRequest { get; private set; } = true;

    internal void FinishedPass(bool madeProgress) => AllowAutomaticRequest = madeProgress;
}

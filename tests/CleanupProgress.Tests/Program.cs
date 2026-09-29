using ImprovedVillagerInventoryCleanup.Behaviour;

// Run with dotnet run --project tests/CleanupProgress.Tests -c Release.
// These are inventory scenarios; no game or IL2CPP process is needed.
static Dictionary<IntPtr, int> Inventory(params (int Id, int Count)[] items) =>
    items.ToDictionary(item => new IntPtr(item.Id), item => item.Count);

var passed = 0;
void Check(string scenario, bool expected, Dictionary<IntPtr, int> before, Dictionary<IntPtr, int> after)
{
    var actual = CleanupProgress.MadeProgress(before, after);
    if (actual != expected) throw new Exception($"FAIL: {scenario}: expected {expected}, got {actual}");
    Console.WriteLine($"PASS: {scenario}");
    passed++;
}

Check("A failed deposit must not trigger another immediate pass", false,
    Inventory((1, 1), (2, 10)), Inventory((1, 1), (2, 10)));
Check("A deposited tool allows cleanup of the remaining material", true,
    Inventory((1, 1), (2, 10)), Inventory((2, 10)));
Check("A partial stack deposit counts even though the number of stacks is unchanged", true,
    Inventory((2, 10)), Inventory((2, 4)));
Check("Newly acquired items do not hide a deposited tool", true,
    Inventory((1, 1), (2, 10)), Inventory((2, 10), (3, 1)));
Check("A growing material stack is not cleanup progress", false,
    Inventory((2, 10)), Inventory((2, 12)));
Check("Adding new items alone must not create a cleanup loop", false,
    Inventory((2, 10)), Inventory((2, 10), (3, 1)));
Check("An empty pass has nothing to continue", false, Inventory(), Inventory());
Check("The last item leaving is recorded as progress", true, Inventory((1, 1)), Inventory());

// Consecutive passes: removing tools and part of a stack continues; an
// unchanged inventory after the next attempt must stop immediate retries.
var passes = new[] { Inventory((1, 1), (2, 1), (3, 8)), Inventory((2, 1), (3, 8)),
    Inventory((3, 8)), Inventory((3, 2)), Inventory((3, 2)) };
for (var i = 1; i < passes.Length; i++)
    Check($"Multi-pass cleanup {i}", i < passes.Length - 1, passes[i - 1], passes[i]);

Console.WriteLine($"{passed} cleanup progress scenarios passed.");

var flow = new CleanupFlowState();
if (!flow.AllowAutomaticRequest) throw new Exception("The first automatic cleanup must be allowed.");
flow.FinishedPass(madeProgress: false);
if (flow.AllowAutomaticRequest) throw new Exception("A no-progress pass must disable automatic requests.");
flow.FinishedPass(madeProgress: false);
if (flow.AllowAutomaticRequest) throw new Exception("Another failed game-triggered pass must stay on vanilla timing.");
flow.FinishedPass(madeProgress: true);
if (!flow.AllowAutomaticRequest) throw new Exception("A productive game-triggered pass must restore continuation.");
flow.FinishedPass(madeProgress: false);
if (flow.AllowAutomaticRequest) throw new Exception("A later no-progress pass must stop automatic requests again.");
Console.WriteLine("PASS: per-villager vanilla timing after no progress and recovery after progress");

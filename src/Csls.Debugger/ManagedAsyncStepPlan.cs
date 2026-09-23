namespace Csls.Debugger;

/// <summary>
/// Describes the next asynchronous suspension reachable by one source step.
/// </summary>
/// <param name="Module">The borrowed runtime module containing both step locations.</param>
/// <param name="MethodToken">The state-machine method containing the yield offset.</param>
/// <param name="AwaitPoint">The compiler-recorded yield and resume locations.</param>
internal readonly record struct ManagedAsyncStepPlan(
    nint Module,
    uint MethodToken,
    ManagedAsyncAwaitPoint AwaitPoint);

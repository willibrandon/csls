using System.Runtime.CompilerServices;

namespace Csls.Debugger;

/// <summary>
/// Performs a suspended dbgshim launch and validates ownership of the target ICorDebug instance.
/// </summary>
internal static class CorDebugRuntimeActivator
{
    /// <summary>
    /// Launches a real target suspended and verifies that its CoreCLR debugging interface activates.
    /// </summary>
    /// <param name="options">The concrete target invocation to probe.</param>
    /// <param name="cancellationToken">Cancels activation and terminates the owned target.</param>
    /// <returns>The operating-system identifier of the successfully activated probe target.</returns>
    internal static async Task<uint> VerifyAsync(
        DebuggeeLaunchOptions options,
        CancellationToken cancellationToken)
    {
        var callbackActor = new DebuggerSessionActor();
        await using ConfiguredAsyncDisposable callbackActorScope =
            callbackActor.ConfigureAwait(false);
        CorDebugDebuggee debuggee = await CorDebugDebuggee.LaunchAsync(
            options,
            callbackActor,
            cancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable debuggeeScope = debuggee.ConfigureAwait(false);
        return checked((uint)debuggee.Id);
    }
}

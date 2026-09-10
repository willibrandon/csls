using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Associates one successfully completed enumeration with its original receiver and lifetime.
/// </summary>
/// <param name="Receiver">The owned identity of the original enumerable.</param>
/// <param name="Generation">The stop generation owning the materialized values.</param>
/// <param name="VariablesReference">The immutable result container reference.</param>
/// <param name="Lifetime">The retirement state shared by its descendants.</param>
internal sealed record ManagedResultsViewSnapshot(
    ManagedResultsViewReceiverIdentity Receiver,
    DebugStopGeneration Generation,
    int VariablesReference,
    ManagedResultsViewLifetime Lifetime);

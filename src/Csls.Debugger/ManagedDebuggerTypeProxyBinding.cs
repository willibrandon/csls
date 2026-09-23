namespace Csls.Debugger;

/// <summary>
/// Owns the runtime constructor and closed type arguments for one debugger proxy.
/// </summary>
/// <param name="Function">The retained ICorDebugFunction constructor pointer.</param>
/// <param name="TypeArguments">The retained ICorDebugType arguments for construction.</param>
internal sealed record ManagedDebuggerTypeProxyBinding(
    nint Function,
    nint[] TypeArguments);

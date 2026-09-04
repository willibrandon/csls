namespace Csls.Debugger;

/// <summary>
/// Owns a resolved constructor and the exact generic type arguments passed to CoreCLR.
/// </summary>
/// <param name="Function">The owned ICorDebugFunction constructor pointer.</param>
/// <param name="TypeArguments">The owned ICorDebugType pointers for the declaring type.</param>
internal sealed record ManagedConstructorBinding(
    nint Function,
    nint[] TypeArguments);

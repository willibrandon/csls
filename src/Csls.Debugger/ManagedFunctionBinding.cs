namespace Csls.Debugger;

/// <summary>
/// Owns a resolved call and its exact declaring arguments and result declaration.
/// </summary>
/// <param name="Function">The owned ICorDebugFunction pointer.</param>
/// <param name="TypeArguments">The owned ICorDebugType pointers for the declaring type.</param>
/// <param name="DeclaredResultType">The bound result declaration, or null for a void method.</param>
internal sealed record ManagedFunctionBinding(
    nint Function,
    nint[] TypeArguments,
    ManagedBoundType? DeclaredResultType);

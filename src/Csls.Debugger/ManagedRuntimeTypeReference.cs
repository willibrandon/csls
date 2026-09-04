namespace Csls.Debugger;

/// <summary>
/// Describes one parsed managed runtime type used by CoreCLR function evaluation.
/// </summary>
/// <param name="MetadataName">The CLR metadata name of the type definition.</param>
/// <param name="TypeArguments">The recursively parsed generic type arguments.</param>
/// <param name="ArrayRanks">The array ranks applied from innermost to outermost.</param>
/// <param name="DebuggerTypeName">The normalized debugger-facing type identity.</param>
internal sealed record ManagedRuntimeTypeReference(
    string MetadataName,
    IReadOnlyList<ManagedRuntimeTypeReference> TypeArguments,
    IReadOnlyList<int> ArrayRanks,
    string DebuggerTypeName);

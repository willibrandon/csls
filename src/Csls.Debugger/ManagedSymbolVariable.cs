namespace Csls.Debugger;

/// <summary>
/// Describes one source variable and its declared tuple-name transforms.
/// </summary>
/// <param name="Name">The source variable name.</param>
/// <param name="TupleCustomTypeInfo">The optional tuple-name transform metadata.</param>
internal sealed record ManagedSymbolVariable(
    string Name,
    ManagedTupleCustomTypeInfo? TupleCustomTypeInfo);

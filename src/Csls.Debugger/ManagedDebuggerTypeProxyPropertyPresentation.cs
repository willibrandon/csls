using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Describes the final-generation rows contributed by one debugger proxy property.
/// </summary>
/// <param name="Name">The metadata property name and ordinal sort key.</param>
/// <param name="IsStatic">Whether the property belongs in the static member group.</param>
/// <param name="Variables">The ordinary property row or flattened root-hidden children.</param>
internal sealed record ManagedDebuggerTypeProxyPropertyPresentation(
    string Name,
    bool IsStatic,
    IReadOnlyList<DebugVariableInfo> Variables);

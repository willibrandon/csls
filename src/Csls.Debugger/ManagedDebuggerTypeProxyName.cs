namespace Csls.Debugger;

/// <summary>
/// Identifies one proxy type definition from an assembly-qualified reflection name.
/// </summary>
/// <param name="MetadataName">The full CLR metadata name including generic arity.</param>
/// <param name="AssemblyName">The optional declaring assembly simple name.</param>
/// <param name="IsConstructed">Whether the attribute names a constructed generic type.</param>
internal sealed record ManagedDebuggerTypeProxyName(
    string MetadataName,
    string? AssemblyName,
    bool IsConstructed);

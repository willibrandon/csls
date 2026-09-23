using System.Reflection.Metadata;

namespace Csls.Debugger;

/// <summary>
/// Identifies one bounded debugger proxy type from a parsed reflection name.
/// </summary>
internal sealed class ManagedDebuggerTypeProxyName
{
    /// <summary>
    /// Creates one validated proxy type identity.
    /// </summary>
    /// <param name="parsedName">The bounded upstream reflection-name tree.</param>
    internal ManagedDebuggerTypeProxyName(TypeName parsedName)
    {
        ArgumentNullException.ThrowIfNull(parsedName);
        ParsedName = parsedName;
        TypeName definition = parsedName.IsConstructedGenericType
            ? parsedName.GetGenericTypeDefinition()
            : parsedName;
        MetadataName = definition.FullName;
        AssemblyName = parsedName.AssemblyName?.Name ?? definition.AssemblyName?.Name;
        IsConstructed = parsedName.IsConstructedGenericType;
    }

    /// <summary>
    /// Gets the bounded upstream reflection-name tree.
    /// </summary>
    internal TypeName ParsedName { get; }

    /// <summary>
    /// Gets the full CLR metadata name including generic arity.
    /// </summary>
    internal string MetadataName { get; }

    /// <summary>
    /// Gets the optional declaring assembly simple name.
    /// </summary>
    internal string? AssemblyName { get; }

    /// <summary>
    /// Gets whether the attribute names a constructed generic type.
    /// </summary>
    internal bool IsConstructed { get; }
}

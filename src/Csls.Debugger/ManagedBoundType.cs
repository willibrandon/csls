namespace Csls.Debugger;

/// <summary>
/// Carries an immutable loaded type identity without retaining native debugger pointers.
/// </summary>
/// <param name="ElementType">The canonical runtime element kind.</param>
/// <param name="ModuleId">The exact loaded defining module, or null for an array.</param>
/// <param name="DefinitionToken">The defining metadata token, or zero for an array.</param>
/// <param name="Name">The debugger-facing metadata name.</param>
/// <param name="TypeArguments">The closed generic arguments, or the single array element type.</param>
/// <param name="ArrayRank">The array rank, or zero for a non-array type.</param>
internal sealed record ManagedBoundType(
    uint ElementType,
    int? ModuleId,
    uint DefinitionToken,
    string Name,
    IReadOnlyList<ManagedBoundType> TypeArguments,
    int ArrayRank = 0)
{
    /// <summary>
    /// Gets whether the type denotes a managed object reference rather than unboxed storage.
    /// </summary>
    internal bool IsReference => ElementType is 0x0e or 0x12 or 0x14 or 0x1c or 0x1d;

    /// <summary>
    /// Gets whether the type denotes an array with one element-type argument.
    /// </summary>
    internal bool IsArray => ElementType is 0x14 or 0x1d;

    /// <summary>
    /// Gets a diagnostic name including every closed generic argument.
    /// </summary>
    internal string DisplayName => IsArray || TypeArguments.Count == 0
        ? Name : $"{Name}<{string.Join(", ", TypeArguments.Select(static argument => argument.DisplayName))}>";

    /// <summary>
    /// Compares exact module, definition, closed arguments, and array shape identities.
    /// </summary>
    internal bool IsSameType(ManagedBoundType other) =>
        ElementType == other.ElementType && ModuleId == other.ModuleId &&
        DefinitionToken == other.DefinitionToken && ArrayRank == other.ArrayRank &&
        TypeArguments.Count == other.TypeArguments.Count &&
        TypeArguments.Zip(other.TypeArguments).All(static pair => pair.First.IsSameType(pair.Second));
}

namespace Csls.Debugger;

/// <summary>
/// Projects tuple-name transforms onto flattened logical ValueTuple elements.
/// </summary>
internal sealed class ManagedTupleTypeProjection
{
    /// <summary>
    /// Creates one validated tuple type projection.
    /// </summary>
    /// <param name="elementNames">The debugger-facing logical element names.</param>
    /// <param name="elementCustomTypeInfo">Tuple metadata projected onto each element type.</param>
    /// <param name="hasAuthoredElementNames">Whether any logical element has an authored name.</param>
    internal ManagedTupleTypeProjection(
        IReadOnlyList<string> elementNames,
        IReadOnlyList<ManagedTupleCustomTypeInfo?> elementCustomTypeInfo,
        bool hasAuthoredElementNames)
    {
        ArgumentNullException.ThrowIfNull(elementNames);
        ArgumentNullException.ThrowIfNull(elementCustomTypeInfo);
        if (elementNames.Count != elementCustomTypeInfo.Count)
        {
            throw new ArgumentException(
                "Tuple element names and nested metadata must have the same cardinality.",
                nameof(elementCustomTypeInfo));
        }

        ElementNames = [.. elementNames];
        ElementCustomTypeInfo = [.. elementCustomTypeInfo];
        HasAuthoredElementNames = hasAuthoredElementNames;
    }

    /// <summary>
    /// Gets the debugger-facing logical element names.
    /// </summary>
    internal IReadOnlyList<string> ElementNames { get; }

    /// <summary>
    /// Gets tuple metadata projected onto each logical element type.
    /// </summary>
    internal IReadOnlyList<ManagedTupleCustomTypeInfo?> ElementCustomTypeInfo { get; }

    /// <summary>
    /// Gets whether any logical element has an authored name.
    /// </summary>
    internal bool HasAuthoredElementNames { get; }
}

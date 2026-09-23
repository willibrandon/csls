using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Tracks bounded logical and physical progress during one object expansion.
/// </summary>
internal sealed class ManagedObjectExpansionState
{
    /// <summary>
    /// Gets the semantic child category selected before logical pagination.
    /// </summary>
    internal DebugVariableFilter Filter { get; init; }

    /// <summary>
    /// Gets the snapshot retirement state shared by all retained expansion descendants.
    /// </summary>
    internal ManagedResultsViewLifetime? Lifetime { get; init; }

    /// <summary>
    /// Gets or sets the number of physical fields inspected across flattened objects.
    /// </summary>
    internal int PhysicalFieldCount { get; set; }

    /// <summary>
    /// Gets or sets the zero-based position among children matching the selected category.
    /// </summary>
    internal int VisibleIndex { get; set; }

    /// <summary>
    /// Gets or sets whether debugger metadata transformed the default view.
    /// </summary>
    internal bool WasTransformed { get; set; }

    /// <summary>
    /// Tests whether a source-classified child belongs in the selected logical page.
    /// </summary>
    /// <param name="isIndexed">Whether the child originates from indexed runtime storage.</param>
    /// <returns>True when the child's category matches the active filter.</returns>
    internal bool Includes(bool isIndexed) => Filter switch
    {
        DebugVariableFilter.All => true,
        DebugVariableFilter.Named => !isIndexed,
        DebugVariableFilter.Indexed => isIndexed,
        _ => throw new InvalidOperationException("The variable filter is invalid.")
    };
}

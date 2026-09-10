namespace Csls.Debugger;

/// <summary>
/// Carries the owned runtime tuple layer used during flattened traversal.
/// </summary>
internal sealed class ManagedTupleTraversalState
{
    /// <summary>
    /// Creates tuple traversal state for one retained runtime layer.
    /// </summary>
    /// <param name="currentValue">The retained ICorDebugValue pointer.</param>
    /// <param name="currentType">The retained ICorDebugType pointer.</param>
    /// <param name="layerEvaluateName">The optional physical expression for the layer.</param>
    /// <param name="origin">The exact physical storage of the current layer.</param>
    internal ManagedTupleTraversalState(
        nint currentValue,
        nint currentType,
        string? layerEvaluateName,
        ManagedValueOrigin? origin = null)
    {
        CurrentValue = currentValue;
        CurrentType = currentType;
        LayerEvaluateName = layerEvaluateName;
        Origin = origin;
    }

    /// <summary>
    /// Gets or sets the retained ICorDebugValue pointer for the current tuple layer.
    /// </summary>
    internal nint CurrentValue { get; set; }

    /// <summary>
    /// Gets or sets the retained ICorDebugType pointer for the current tuple layer.
    /// </summary>
    internal nint CurrentType { get; set; }

    /// <summary>
    /// Gets or sets the optional physical expression for the current tuple layer.
    /// </summary>
    internal string? LayerEvaluateName { get; set; }

    /// <summary>
    /// Gets or sets the exact physical storage of the current tuple layer.
    /// </summary>
    internal ManagedValueOrigin? Origin { get; set; }
}

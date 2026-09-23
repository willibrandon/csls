namespace Csls.Debugger;

/// <summary>
/// Identifies a retained value by physical storage when available, or by COM identity otherwise.
/// </summary>
/// <param name="Identity">The COM identity for values without a physical origin.</param>
/// <param name="FrameId">The source frame used to inspect the value.</param>
/// <param name="EvaluateName">The source expression associated with the value.</param>
/// <param name="View">The value's presentation mode.</param>
/// <param name="Origin">The exact physical storage, when known.</param>
/// <param name="Lifetime">The owning Results View snapshot, when present.</param>
/// <param name="TupleCustomTypeInfo">The authored tuple-name transform.</param>
internal readonly record struct ManagedRetainedValueKey(
    nint Identity,
    int? FrameId,
    string? EvaluateName,
    ManagedValueView View,
    ManagedValueOrigin? Origin,
    ManagedResultsViewLifetime? Lifetime,
    ManagedTupleCustomTypeInfo? TupleCustomTypeInfo);

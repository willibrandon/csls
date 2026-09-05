using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Owns a resolved writable location and its declaration metadata for one stopped operation.
/// </summary>
internal sealed class ManagedAssignmentTarget : IDisposable
{
    private nint _pointer;

    private ManagedAssignmentTarget(
        nint pointer,
        ManagedTupleCustomTypeInfo? tupleCustomTypeInfo,
        ManagedValueOrigin? origin,
        string? evaluateName)
    {
        _pointer = pointer;
        TupleCustomTypeInfo = tupleCustomTypeInfo;
        Origin = origin;
        EvaluateName = evaluateName;
    }

    /// <summary>
    /// Gets the owned writable runtime value.
    /// </summary>
    internal nint Pointer => _pointer != 0
        ? _pointer
        : throw new ObjectDisposedException(nameof(ManagedAssignmentTarget));

    /// <summary>
    /// Gets the tuple names declared at the destination.
    /// </summary>
    internal ManagedTupleCustomTypeInfo? TupleCustomTypeInfo { get; }

    /// <summary>
    /// Gets the physical storage identified before mutation.
    /// </summary>
    internal ManagedValueOrigin? Origin { get; }

    /// <summary>
    /// Gets the destination expression with already evaluated array indices.
    /// </summary>
    internal string? EvaluateName { get; }

    /// <summary>
    /// Adopts the native reference even if allocating its managed owner fails.
    /// </summary>
    internal static ManagedAssignmentTarget TakeOwnership(
        (nint Value, ManagedTupleCustomTypeInfo? TupleCustomTypeInfo, ManagedValueOrigin? Origin) value,
        string? evaluateName)
    {
        try
        {
            return new ManagedAssignmentTarget(value.Value, value.TupleCustomTypeInfo, value.Origin, evaluateName);
        }
        catch
        {
            _ = ComAbi.Release(value.Value);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        nint pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
        {
            _ = ComAbi.Release(pointer);
        }
    }
}

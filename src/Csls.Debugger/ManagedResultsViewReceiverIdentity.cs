namespace Csls.Debugger;

/// <summary>
/// Owns a receiver's stable physical path and optional independently rooted heap owner.
/// </summary>
internal sealed class ManagedResultsViewReceiverIdentity : IDisposable
{
    private nint _heapHandle;
    private readonly Action<nint> _releaseHandle;

    /// <summary>
    /// Creates a receiver identity before target execution invalidates inspection handles.
    /// </summary>
    /// <param name="origin">The pointer-free physical path with a normalized heap root.</param>
    /// <param name="heapHandle">The independently owned strong heap handle, or zero.</param>
    /// <param name="releaseHandle">Releases native ownership according to the current runtime state.</param>
    internal ManagedResultsViewReceiverIdentity(
        ManagedValueOrigin? origin, nint heapHandle, Action<nint> releaseHandle)
    {
        ArgumentNullException.ThrowIfNull(releaseHandle);
        Origin = origin;
        _heapHandle = heapHandle;
        _releaseHandle = releaseHandle;
    }

    /// <summary>
    /// Gets the physical path that remains valid during debugger function evaluation.
    /// </summary>
    internal ManagedValueOrigin? Origin { get; }

    /// <summary>
    /// Gets the strong handle whose current referent follows garbage collection.
    /// </summary>
    internal nint HeapHandle => _heapHandle;

    /// <summary>
    /// Transfers native ownership to the debuggee's runtime-aware release operation.
    /// </summary>
    /// <returns>The owned strong handle, or zero after release.</returns>
    internal nint DetachHeapHandle() => Interlocked.Exchange(ref _heapHandle, 0);

    /// <inheritdoc />
    public void Dispose() => _releaseHandle(DetachHeapHandle());
}

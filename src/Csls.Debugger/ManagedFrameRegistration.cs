namespace Csls.Debugger;

/// <summary>
/// Commits one actor-owned frame inspection or releases its unpublished registrations.
/// </summary>
internal sealed class ManagedFrameRegistration : IDisposable
{
    private readonly ManagedStoppedFrameRegistry _registry;
    private readonly int _lastFrameId;
    private readonly int _lastInstructionAddressId;
    private bool _completed;

    /// <summary>
    /// Captures the monotonic identifiers preceding a frame inspection.
    /// </summary>
    /// <param name="registry">The actor-owned frame and instruction registry.</param>
    /// <param name="lastFrameId">The last allocated logical frame identifier.</param>
    /// <param name="lastInstructionAddressId">The last allocated native binding identifier.</param>
    internal ManagedFrameRegistration(ManagedStoppedFrameRegistry registry, int lastFrameId, int lastInstructionAddressId)
    {
        _registry = registry;
        _lastFrameId = lastFrameId;
        _lastInstructionAddressId = lastInstructionAddressId;
    }

    /// <summary>
    /// Preserves registrations belonging to the successfully completed inspection.
    /// </summary>
    internal void Commit() => _completed = true;

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_completed)
        {
            _completed = true;
            _registry.RollbackRegistration(_lastFrameId, _lastInstructionAddressId);
        }
    }
}

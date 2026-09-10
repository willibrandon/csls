namespace Csls.Debugger;

/// <summary>
/// Tracks assignment attempts independently of target execution and stopped-frame generations.
/// </summary>
internal sealed class ManagedVariableMutationState
{
    private long _revision;

    /// <summary>
    /// Gets the session-wide revision visible to protocol adapters after an assignment attempt.
    /// </summary>
    internal long Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// Advances on the session actor before a prepared assignment can invalidate inspected values.
    /// </summary>
    internal void Advance() => Volatile.Write(ref _revision, checked(_revision + 1));
}

using Csls.Debugger.Interop;
using System.Diagnostics.CodeAnalysis;

namespace Csls.Debugger;

/// <summary>
/// Owns current native frames and logical identities for one application-level stopped interval.
/// </summary>
internal sealed class ManagedStoppedFrameRegistry
{
    private const int MaximumFrameCount = 64 * 1024;
    private readonly Dictionary<(int ThreadId, int FrameIndex), ManagedFrameHandle> _positions = [];
    private readonly Dictionary<int, ManagedFrameHandle> _current = [];
    private readonly Dictionary<ManagedFrameIdentity, int> _logicalIds = [];
    private readonly Dictionary<int, ManagedFrameIdentity> _identities = [];
    private int _nextId;

    /// <summary>
    /// Gets the native frame bindings owned by the current runtime generation.
    /// </summary>
    internal IReadOnlyCollection<ManagedFrameHandle> Values => _current.Values;

    /// <summary>
    /// Finds the current native frame at an enumerated stack position.
    /// </summary>
    /// <param name="threadId">The owning managed thread.</param>
    /// <param name="frameIndex">The zero-based managed stack position.</param>
    /// <param name="frame">Receives the current owned native binding.</param>
    /// <returns>True when the position has already been enumerated in this generation.</returns>
    internal bool TryGetByPosition(
        int threadId,
        int frameIndex,
        [NotNullWhen(true)] out ManagedFrameHandle? frame) =>
        _positions.TryGetValue((threadId, frameIndex), out frame);

    /// <summary>
    /// Finds the current native binding for an application-level frame identifier.
    /// </summary>
    /// <param name="frameId">The logical frame identifier.</param>
    /// <param name="frame">Receives the current owned native binding.</param>
    /// <returns>True when the frame has a binding in this runtime generation.</returns>
    internal bool TryGetCurrent(
        int frameId,
        [NotNullWhen(true)] out ManagedFrameHandle? frame) =>
        _current.TryGetValue(frameId, out frame);

    /// <summary>
    /// Gets the exact physical identity required to reacquire an unbound logical frame.
    /// </summary>
    /// <param name="frameId">The logical frame identifier.</param>
    /// <returns>The pointer-free identity retained across debugger-owned evaluation only.</returns>
    internal ManagedFrameIdentity GetIdentity(int frameId) =>
        _identities.TryGetValue(frameId, out ManagedFrameIdentity? identity)
            ? identity
            : throw new InvalidOperationException($"Frame {frameId} is stale or unknown.");

    /// <summary>
    /// Permanently retires an unbound logical frame whose physical activation no longer exists.
    /// </summary>
    /// <param name="frameId">The logical identifier that could not be reacquired.</param>
    internal void RetireIdentity(int frameId)
    {
        if (_identities.Remove(frameId, out ManagedFrameIdentity? identity))
        {
            _logicalIds.Remove(identity);
        }
    }

    /// <summary>
    /// Allocates or reuses a logical identifier for a proven physical activation.
    /// </summary>
    /// <param name="identity">The exact physical identity, or null for an unidentifiable frame.</param>
    /// <returns>A monotonic identifier, reused only for the same live logical identity.</returns>
    internal int GetOrCreateId(ManagedFrameIdentity? identity)
    {
        if (identity is not null && _logicalIds.TryGetValue(identity, out int existing))
        {
            return existing;
        }

        if (_identities.Count >= MaximumFrameCount || _current.Count >= MaximumFrameCount)
        {
            throw new InvalidOperationException(
                $"The stopped interval exceeds the retained-frame limit of {MaximumFrameCount}.");
        }

        return checked(++_nextId);
    }

    /// <summary>
    /// Accepts ownership of a current-generation native frame binding.
    /// </summary>
    /// <param name="frame">The newly retained binding whose COM pointer is transferred.</param>
    /// <param name="identity">The exact physical activation, or null for an unidentifiable frame.</param>
    internal void Add(ManagedFrameHandle frame, ManagedFrameIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_positions.ContainsKey((frame.ThreadId, frame.FrameIndex)) || _current.ContainsKey(frame.Id))
        {
            throw new InvalidOperationException("The physical frame already has a current native binding.");
        }

        bool newIdentity = identity is not null && !_logicalIds.ContainsKey(identity);
        if (identity is not null && !newIdentity && _logicalIds[identity] != frame.Id)
        {
            throw new InvalidOperationException("The physical frame identity has a different logical identifier.");
        }

        // Complete every allocation before transferring ownership or publishing any dictionary entry.
        _positions.EnsureCapacity(_positions.Count + 1);
        _current.EnsureCapacity(_current.Count + 1);
        if (identity is not null && newIdentity)
        {
            _logicalIds.EnsureCapacity(_logicalIds.Count + 1);
            _identities.EnsureCapacity(_identities.Count + 1);
            _logicalIds.Add(identity, frame.Id);
            _identities.Add(frame.Id, identity);
        }

        _positions.Add((frame.ThreadId, frame.FrameIndex), frame);
        _current.Add(frame.Id, frame);
    }

    /// <summary>
    /// Releases every native frame and optionally preserves only pointer-free logical identities.
    /// </summary>
    /// <param name="preserveIdentity">Whether debugger-owned evaluation continues the same application stop.</param>
    internal void Clear(bool preserveIdentity)
    {
        foreach (ManagedFrameHandle frame in _current.Values)
        {
            _ = ComAbi.Release(frame.Pointer);
        }

        _positions.Clear();
        _current.Clear();
        if (!preserveIdentity)
        {
            _logicalIds.Clear();
            _identities.Clear();
        }
    }
}

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
    private readonly Dictionary<string, ManagedInstructionReferenceHandle> _instructions = new(StringComparer.Ordinal);
    private readonly Dictionary<int, ManagedFrameHandle> _instructionAddresses = [];
    private int _nextId;
    private int _nextInstructionAddressId;

    /// <summary>
    /// Gets the native frame bindings owned by the current runtime generation.
    /// </summary>
    internal IReadOnlyCollection<ManagedFrameHandle> Values => _current.Values;

    /// <summary>
    /// Begins an inspection whose new bindings are retained only after successful completion.
    /// </summary>
    /// <returns>An actor-owned registration scope that rolls back unless committed.</returns>
    internal ManagedFrameRegistration BeginRegistration() => new(this, _nextId, _nextInstructionAddressId);

    /// <summary>
    /// Allocates a native-binding identity that is never reused after rollback or execution.
    /// </summary>
    /// <returns>The monotonic instruction-address owner identifier.</returns>
    internal int AllocateInstructionAddressId() => checked(++_nextInstructionAddressId);

    /// <summary>
    /// Finds a generation-owned opaque instruction reference.
    /// </summary>
    /// <param name="reference">The opaque reference supplied by the client.</param>
    /// <param name="location">Receives the instruction location owned by a retained frame.</param>
    /// <returns>True when the reference is registered in the current runtime generation.</returns>
    internal bool TryGetInstruction(string reference, [NotNullWhen(true)] out ManagedInstructionReferenceHandle? location) =>
        _instructions.TryGetValue(reference, out location);

    /// <summary>
    /// Finds a frame through the owner portion of its virtual instruction address.
    /// </summary>
    /// <param name="ownerId">The native binding's instruction-address owner.</param>
    /// <param name="frame">Receives the retained current-generation frame.</param>
    /// <returns>True when the native binding is retained.</returns>
    internal bool TryGetByInstructionAddress(int ownerId, [NotNullWhen(true)] out ManagedFrameHandle? frame) =>
        _instructionAddresses.TryGetValue(ownerId, out frame);

    /// <summary>
    /// Registers an additional instruction location against an already retained native frame.
    /// </summary>
    /// <param name="reference">The new opaque instruction reference.</param>
    /// <param name="frame">The current native frame owning the instruction location.</param>
    /// <param name="ilOffset">The method-body instruction offset.</param>
    internal void AddInstruction(string reference, ManagedFrameHandle frame, uint ilOffset)
    {
        if (!_current.TryGetValue(frame.Id, out ManagedFrameHandle? current) || !ReferenceEquals(frame, current))
        {
            throw new InvalidOperationException("An instruction reference requires a retained current frame.");
        }

        _instructions.Add(reference, new ManagedInstructionReferenceHandle { Frame = frame, IlOffset = ilOffset });
    }

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
        if (_positions.ContainsKey((frame.ThreadId, frame.FrameIndex)) || _current.ContainsKey(frame.Id) ||
            _instructions.ContainsKey(frame.InstructionReference) || _instructionAddresses.ContainsKey(frame.InstructionAddressId))
        {
            throw new InvalidOperationException("The physical frame already has a current native binding.");
        }

        bool newIdentity = identity is not null && !_logicalIds.ContainsKey(identity);
        if (identity is not null && !newIdentity && _logicalIds[identity] != frame.Id)
        {
            throw new InvalidOperationException("The physical frame identity has a different logical identifier.");
        }

        // Complete every allocation before transferring ownership or publishing any dictionary entry.
        var instruction = new ManagedInstructionReferenceHandle { Frame = frame, IlOffset = frame.IlOffset };
        _positions.EnsureCapacity(_positions.Count + 1);
        _current.EnsureCapacity(_current.Count + 1);
        _instructions.EnsureCapacity(_instructions.Count + 1);
        _instructionAddresses.EnsureCapacity(_instructionAddresses.Count + 1);
        if (identity is not null && newIdentity)
        {
            _logicalIds.EnsureCapacity(_logicalIds.Count + 1);
            _identities.EnsureCapacity(_identities.Count + 1);
            _logicalIds.Add(identity, frame.Id);
            _identities.Add(frame.Id, identity);
        }

        _positions.Add((frame.ThreadId, frame.FrameIndex), frame);
        _current.Add(frame.Id, frame);
        _instructions.Add(frame.InstructionReference, instruction);
        _instructionAddresses.Add(frame.InstructionAddressId, frame);
    }

    /// <summary>
    /// Releases newly registered native frames and retires only newly allocated logical identities.
    /// </summary>
    /// <param name="lastFrameId">The last logical frame identifier before the failed inspection.</param>
    /// <param name="lastInstructionAddressId">The last native binding identifier before the failed inspection.</param>
    internal void RollbackRegistration(int lastFrameId, int lastInstructionAddressId)
    {
        foreach (string reference in _instructions
            .Where(pair => pair.Value.Frame.InstructionAddressId > lastInstructionAddressId)
            .Select(static pair => pair.Key).ToArray())
        {
            _instructions.Remove(reference);
        }

        foreach (ManagedFrameHandle frame in _current.Values
            .Where(frame => frame.InstructionAddressId > lastInstructionAddressId).ToArray())
        {
            _positions.Remove((frame.ThreadId, frame.FrameIndex));
            _current.Remove(frame.Id);
            _instructionAddresses.Remove(frame.InstructionAddressId);
            if (frame.Id > lastFrameId)
            {
                RetireIdentity(frame.Id);
            }

            _ = ComAbi.Release(frame.Pointer);
        }
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
        _instructions.Clear();
        _instructionAddresses.Clear();
        if (!preserveIdentity)
        {
            _logicalIds.Clear();
            _identities.Clear();
        }
    }
}

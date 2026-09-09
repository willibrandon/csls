namespace Csls.Debugger;

/// <summary>
/// Retains bounded logical expansion paths and immutable storage independently of the native read cache.
/// </summary>
public sealed class CorDebugDumpValues
{
    private const int FirstReference = 0x40000000;
    private const int MaximumReferences = 65536;
    private readonly List<(CorDebugDumpValuePath Path, CorDebugDumpStorageLocation? Storage)> _paths = [];
    private readonly Dictionary<CorDebugDumpValuePath, int> _references = [];

    /// <summary>
    /// Reports whether an expansion reference belongs to this captured session.
    /// </summary>
    /// <param name="reference">The opaque child-container identifier.</param>
    /// <returns>Whether the handle is currently published.</returns>
    public bool Contains(int reference) => reference >= FirstReference && reference - FirstReference < _paths.Count;

    /// <summary>
    /// Retires every logical path when its captured session closes.
    /// </summary>
    public void Clear()
    {
        _paths.Clear();
        _references.Clear();
    }

    /// <summary>
    /// Gets the current path checkpoint for transactional request publication.
    /// </summary>
    internal int Checkpoint => _paths.Count;

    /// <summary>
    /// Retrieves an existing logical path without touching captured memory.
    /// </summary>
    internal CorDebugDumpValuePath Get(int reference) => Contains(reference)
        ? _paths[reference - FirstReference].Path
        : throw new ArgumentException("The captured value reference is stale or unknown.", nameof(reference));

    /// <summary>
    /// Retrieves captured storage that can be reacquired through its opaque runtime type identity.
    /// </summary>
    internal CorDebugDumpStorageLocation? GetStorage(int reference) => Contains(reference)
        ? _paths[reference - FirstReference].Storage
        : throw new ArgumentException("The captured value reference is stale or unknown.", nameof(reference));

    /// <summary>
    /// Assigns a stable identifier to a captured storage path within the session budget.
    /// </summary>
    internal int Retain(CorDebugDumpValuePath path, CorDebugDumpStorage storage)
    {
        if (_references.TryGetValue(path, out int reference))
        {
            return reference;
        }

        if (_paths.Count == MaximumReferences || path.Depth > 256)
        {
            throw new InvalidDataException("Captured expansion exceeds the limit of 65536 paths or 256 nested selections.");
        }

        reference = checked(FirstReference + _paths.Count);
        _paths.Add((path, storage.TypeId is { } typeId
            ? new CorDebugDumpStorageLocation(typeId, storage.Address, storage.Size, storage.HeapObject)
            : null));
        _references.Add(path, reference);
        return reference;
    }

    /// <summary>
    /// Releases paths allocated by an unsuccessful request while preserving previously published handles.
    /// </summary>
    internal void Rollback(int checkpoint)
    {
        while (_paths.Count > checkpoint)
        {
            _ = _references.Remove(_paths[^1].Path);
            _paths.RemoveAt(_paths.Count - 1);
        }
    }
}

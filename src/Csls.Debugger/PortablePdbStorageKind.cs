namespace Csls.Debugger;

/// <summary>
/// Identifies where matching Portable PDB metadata was resolved.
/// </summary>
internal enum PortablePdbStorageKind
{
    /// <summary>
    /// The Portable PDB is stored in a matching associated file.
    /// </summary>
    AssociatedFile,

    /// <summary>
    /// The Portable PDB is embedded in the managed PE image.
    /// </summary>
    Embedded,

    /// <summary>
    /// The Portable PDB was supplied as a runtime-owned in-memory symbol stream.
    /// </summary>
    InMemory
}

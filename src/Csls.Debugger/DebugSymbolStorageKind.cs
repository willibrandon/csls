namespace Csls.Debugger;

/// <summary>
/// Identifies where matching managed debug symbols were resolved.
/// </summary>
internal enum DebugSymbolStorageKind
{
    /// <summary>
    /// Managed symbols are stored in a matching associated PDB file.
    /// </summary>
    AssociatedFile,

    /// <summary>
    /// The Portable PDB is embedded in the managed PE image.
    /// </summary>
    Embedded,

    /// <summary>
    /// The Portable PDB was supplied as a runtime-owned in-memory symbol stream.
    /// </summary>
    InMemory,

    /// <summary>
    /// Symbols are stored in an identity-matched Windows PDB file.
    /// </summary>
    Windows
}

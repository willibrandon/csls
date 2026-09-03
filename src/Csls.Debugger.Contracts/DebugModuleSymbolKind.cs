namespace Csls.Debugger.Contracts;

/// <summary>
/// Identifies the validated symbol format loaded for a managed module.
/// </summary>
public enum DebugModuleSymbolKind
{
    /// <summary>
    /// No matching symbols are loaded.
    /// </summary>
    None,

    /// <summary>
    /// A matching associated Portable PDB file is loaded.
    /// </summary>
    PortablePdb,

    /// <summary>
    /// A matching Portable PDB embedded in the module is loaded.
    /// </summary>
    EmbeddedPortablePdb,

    /// <summary>
    /// A Portable PDB supplied by the runtime for an in-memory module is loaded.
    /// </summary>
    InMemoryPortablePdb
}

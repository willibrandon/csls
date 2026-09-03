using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Carries one Portable PDB document and its optional embedded source bytes.
/// </summary>
internal sealed class PortablePdbSourceDocument
{
    /// <summary>
    /// Gets the exact document path stored in the Portable PDB.
    /// </summary>
    internal required string Path { get; init; }

    /// <summary>
    /// Gets the DAP-compatible document checksum when available.
    /// </summary>
    internal DebugSourceChecksum? Checksum { get; init; }

    /// <summary>
    /// Gets the uncompressed embedded source bytes when available.
    /// </summary>
    internal byte[]? EmbeddedSource { get; init; }
}

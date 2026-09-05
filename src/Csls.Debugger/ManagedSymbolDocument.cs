using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Carries one debugger symbol document and its validated source metadata.
/// </summary>
internal sealed class ManagedSymbolDocument
{
    /// <summary>
    /// Gets the exact document path stored in the symbol file.
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

    /// <summary>
    /// Gets the resolved Source Link URI when the symbols provide one.
    /// </summary>
    internal Uri? SourceLinkUri { get; init; }
}

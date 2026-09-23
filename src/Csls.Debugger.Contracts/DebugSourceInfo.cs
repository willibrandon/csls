namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one source document represented by loaded debugger symbols.
/// </summary>
/// <param name="Name">The source document display name.</param>
/// <param name="Path">The verified source path, or null when no local source is usable.</param>
/// <param name="SourceReference">The positive session-local content reference, or zero.</param>
/// <param name="Origin">The source-content origin when supplied by the debugger.</param>
/// <param name="Checksum">The Portable PDB source checksum when supported by DAP.</param>
public sealed record DebugSourceInfo(
    string Name,
    string? Path,
    int SourceReference,
    string? Origin,
    DebugSourceChecksum? Checksum);

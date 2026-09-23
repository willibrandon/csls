namespace Csls.Debugger;

/// <summary>
/// Describes one identity-validated symbol file selected for a managed module.
/// </summary>
/// <param name="StorageKind">The resolved symbol storage kind.</param>
/// <param name="Path">The associated PDB path, or null for embedded symbols.</param>
internal sealed record DebugSymbolResolution(
    DebugSymbolStorageKind StorageKind,
    string? Path);

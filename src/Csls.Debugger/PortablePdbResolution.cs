namespace Csls.Debugger;

/// <summary>
/// Describes one identity-validated Portable PDB selected for a managed module.
/// </summary>
/// <param name="StorageKind">The resolved symbol storage kind.</param>
/// <param name="Path">The associated PDB path, or null for embedded symbols.</param>
internal sealed record PortablePdbResolution(
    PortablePdbStorageKind StorageKind,
    string? Path);

namespace Csls.Debugger;

/// <summary>
/// Identifies an associated PDB from one managed PE CodeView record.
/// </summary>
/// <param name="FileName">The portable PDB file name.</param>
/// <param name="OriginalPath">The compiler-recorded PDB path.</param>
/// <param name="Signature">The CodeView content signature.</param>
/// <param name="Age">The CodeView PDB age.</param>
internal sealed record PortablePdbReference(
    string FileName,
    string OriginalPath,
    Guid Signature,
    int Age)
{
    /// <summary>
    /// Gets the Portable PDB symbol-server identity segment.
    /// </summary>
    internal string PortableIdentity => $"{Signature:N}FFFFFFFF";

    /// <summary>
    /// Gets the Windows PDB symbol-server identity segment.
    /// </summary>
    internal string WindowsIdentity => $"{Signature:N}{Age:x}";
}

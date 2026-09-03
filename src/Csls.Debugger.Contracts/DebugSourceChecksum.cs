namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes a Portable PDB source-document checksum.
/// </summary>
/// <param name="Algorithm">The DAP-compatible checksum algorithm name.</param>
/// <param name="Value">The uppercase hexadecimal checksum value.</param>
public sealed record DebugSourceChecksum(string Algorithm, string Value);

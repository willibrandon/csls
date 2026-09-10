namespace Csls.Debugger;

/// <summary>
/// Preserves the distinction between vector and multidimensional array signatures.
/// </summary>
/// <param name="Rank">The number of array dimensions.</param>
/// <param name="IsVector">Whether the signature denotes a zero-based vector.</param>
internal readonly record struct ManagedMetadataArrayShape(int Rank, bool IsVector);

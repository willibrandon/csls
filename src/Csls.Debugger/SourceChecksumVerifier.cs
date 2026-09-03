using Csls.Debugger.Contracts;
using System.Security.Cryptography;

namespace Csls.Debugger;

/// <summary>
/// Verifies debugger source bytes against Portable PDB checksums.
/// </summary>
internal static class SourceChecksumVerifier
{
    /// <summary>
    /// Determines whether source bytes match a supported Portable PDB checksum.
    /// </summary>
    /// <param name="source">The exact source bytes.</param>
    /// <param name="checksum">The expected checksum.</param>
    /// <returns>True when the checksum algorithm is supported and the value matches.</returns>
    internal static bool Matches(byte[] source, DebugSourceChecksum checksum)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(checksum);
        if (checksum.Algorithm is not ("SHA1" or "SHA256"))
        {
            return false;
        }

        using var hash = IncrementalHash.CreateHash(
            new HashAlgorithmName(checksum.Algorithm));
        hash.AppendData(source);
        return string.Equals(
            Convert.ToHexString(hash.GetHashAndReset()),
            checksum.Value,
            StringComparison.OrdinalIgnoreCase);
    }
}

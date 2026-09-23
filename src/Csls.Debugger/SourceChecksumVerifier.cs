using Csls.Debugger.Contracts;
using System.Security.Cryptography;

namespace Csls.Debugger;

/// <summary>
/// Verifies debugger source bytes against managed PDB checksums.
/// </summary>
internal static class SourceChecksumVerifier
{
    private const int MaximumLocalSourceBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Validates a bounded seekable local file against its optional managed PDB checksum.
    /// </summary>
    /// <param name="path">The resolved local source path.</param>
    /// <param name="checksum">The expected checksum, when supplied by the symbols.</param>
    /// <returns>The local file's readability and source checksum identity.</returns>
    internal static LocalSourceStatus InspectFile(string path, DebugSourceChecksum? checksum)
    {
        try
        {
            using FileStream stream = DebuggerInputFile.OpenRead(path);
            if (!stream.CanSeek || stream.Length > MaximumLocalSourceBytes)
            {
                return LocalSourceStatus.Unavailable;
            }

            if (checksum is null)
            {
                return LocalSourceStatus.Unverified;
            }

            if (checksum.Algorithm is not ("SHA1" or "SHA256"))
            {
                return LocalSourceStatus.Unverified;
            }

            using var hash = IncrementalHash.CreateHash(new HashAlgorithmName(checksum.Algorithm));
            Span<byte> buffer = stackalloc byte[4096];
            int total = 0;
            while (true)
            {
                int read = stream.Read(buffer[..Math.Min(buffer.Length, MaximumLocalSourceBytes + 1 - total)]);
                if (read == 0)
                {
                    return string.Equals(Convert.ToHexString(hash.GetHashAndReset()), checksum.Value,
                        StringComparison.OrdinalIgnoreCase) ? LocalSourceStatus.Verified : LocalSourceStatus.Mismatch;
                }

                total += read;
                if (total > MaximumLocalSourceBytes)
                {
                    return LocalSourceStatus.Unavailable;
                }

                hash.AppendData(buffer[..read]);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return LocalSourceStatus.Unavailable;
        }
    }

    /// <summary>
    /// Determines whether source bytes match a supported managed PDB checksum.
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

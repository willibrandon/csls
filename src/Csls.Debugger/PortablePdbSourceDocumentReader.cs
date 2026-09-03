using Csls.Debugger.Contracts;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Security.Cryptography;

namespace Csls.Debugger;

/// <summary>
/// Reads and validates checksummed source documents from Portable PDB metadata.
/// </summary>
internal static class PortablePdbSourceDocumentReader
{
    private const int MaximumSourceBytes = 32 * 1024 * 1024;
    private static readonly Guid s_embeddedSourceKind =
        new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    private static readonly Guid s_sha1Algorithm =
        new("FF1816EC-AA5E-4D10-87F7-6F4963833460");
    private static readonly Guid s_sha256Algorithm =
        new("8829D00F-11B8-4213-878B-770E8597AC16");

    /// <summary>
    /// Reads one document and rejects malformed or checksum-invalid embedded source.
    /// </summary>
    /// <param name="reader">The owning Portable PDB metadata reader.</param>
    /// <param name="handle">The selected document handle.</param>
    /// <returns>The validated source-document metadata.</returns>
    internal static PortablePdbSourceDocument Read(
        MetadataReader reader,
        DocumentHandle handle)
    {
        Document document = reader.GetDocument(handle);
        string path = reader.GetString(document.Name);
        DebugSourceChecksum? checksum = ReadChecksum(reader, document);
        byte[]? embeddedSource = ReadEmbeddedSource(reader, handle);
        if (embeddedSource is not null && checksum is not null &&
            !ChecksumMatches(embeddedSource, checksum))
        {
            throw new BadImageFormatException(
                $"Embedded source for '{path}' does not match its Portable PDB checksum.");
        }

        return new PortablePdbSourceDocument
        {
            Path = path,
            Checksum = checksum,
            EmbeddedSource = embeddedSource
        };
    }

    private static DebugSourceChecksum? ReadChecksum(
        MetadataReader reader,
        Document document)
    {
        if (document.HashAlgorithm.IsNil || document.Hash.IsNil)
        {
            return null;
        }

        Guid algorithm = reader.GetGuid(document.HashAlgorithm);
        string? name = algorithm == s_sha256Algorithm
            ? "SHA256"
            : algorithm == s_sha1Algorithm ? "SHA1" : null;
        if (name is null)
        {
            return null;
        }

        return new DebugSourceChecksum(
            name,
            Convert.ToHexString(reader.GetBlobBytes(document.Hash)));
    }

    private static byte[]? ReadEmbeddedSource(
        MetadataReader reader,
        DocumentHandle document)
    {
        foreach (CustomDebugInformationHandle handle in
            reader.GetCustomDebugInformation(document))
        {
            CustomDebugInformation information = reader.GetCustomDebugInformation(handle);
            if (reader.GetGuid(information.Kind) != s_embeddedSourceKind)
            {
                continue;
            }

            return DecodeEmbeddedSource(reader.GetBlobBytes(information.Value));
        }

        return null;
    }

    private static byte[] DecodeEmbeddedSource(byte[] blob)
    {
        if (blob.Length < sizeof(int))
        {
            throw new BadImageFormatException("An embedded source record is truncated.");
        }

        int uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(blob);
        if (uncompressedSize < 0 || uncompressedSize > MaximumSourceBytes ||
            uncompressedSize == 0 && blob.Length - sizeof(int) > MaximumSourceBytes)
        {
            throw new BadImageFormatException("An embedded source record exceeds its size limit.");
        }

        if (uncompressedSize == 0)
        {
            return blob[sizeof(int)..];
        }

        using var compressed = new MemoryStream(blob, sizeof(int), blob.Length - sizeof(int));
        using var deflate = new DeflateStream(compressed, CompressionMode.Decompress);
        using var source = new MemoryStream(uncompressedSize);
        deflate.CopyTo(source);
        if (source.Length != uncompressedSize)
        {
            throw new BadImageFormatException(
                "An embedded source record has an invalid uncompressed size.");
        }

        return source.ToArray();
    }

    private static bool ChecksumMatches(byte[] source, DebugSourceChecksum checksum)
    {
        if (checksum.Algorithm != "SHA256")
        {
            return true;
        }

        byte[] actual = SHA256.HashData(source);
        return string.Equals(
            Convert.ToHexString(actual),
            checksum.Value,
            StringComparison.OrdinalIgnoreCase);
    }
}

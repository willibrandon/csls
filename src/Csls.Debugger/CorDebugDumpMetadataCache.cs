using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Csls.Debugger;

/// <summary>
/// Retains bounded, identity-checked module snapshots until the native dump process releases its metadata readers.
/// </summary>
internal sealed class CorDebugDumpMetadataCache : IDisposable
{
    private const long MaximumImageBytes = 512 * 1024 * 1024;
    private const long MaximumRetainedBytes = 1024 * 1024 * 1024;
    private readonly Dictionary<(string ImagePath, uint Timestamp, uint ImageSize), string> _paths = [];
    private string? _directory;
    private long _retainedBytes;
    private bool _disposed;

    /// <summary>
    /// Resolves and snapshots the requested module using its recorded PE build identity.
    /// </summary>
    /// <param name="source">The captured target's local image discovery policy.</param>
    /// <param name="imagePath">The recorded module name.</param>
    /// <param name="timestamp">The required PE timestamp.</param>
    /// <param name="imageSize">The required mapped-image size.</param>
    /// <param name="operation">The active native inspection request.</param>
    /// <returns>The owned immutable image path.</returns>
    internal string Resolve(ICorDebugDumpSource source, string imagePath, uint timestamp, uint imageSize,
        CorDebugDumpReadOperation? operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        operation?.ThrowIfInterrupted();
        (string, uint, uint) key = (imagePath, timestamp, imageSize);
        if (_paths.TryGetValue(key, out string? retained))
        {
            return retained;
        }

        if (_paths.Count >= 4096)
        {
            throw new InvalidDataException("Captured metadata exceeds the 4096-module retention limit.");
        }

        int candidates = 0;
        foreach (string candidate in source.FindMetadataImages(imagePath))
        {
            operation?.ThrowIfInterrupted();
            if (++candidates > 4096)
            {
                throw new InvalidDataException("Captured metadata discovery exceeds the 4096-image limit.");
            }

            if (!IsLocalPath(candidate))
            {
                continue;
            }

            try
            {
                string? path = CaptureCandidate(candidate, key, operation);
                if (path is not null)
                {
                    return path;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                // Other local candidates may contain the exact image requested by the captured runtime.
                operation?.ThrowIfInterrupted();
                continue;
            }
        }

        throw new FileNotFoundException($"The matching metadata image for '{imagePath}' was not found locally.");
    }

    private string? CaptureCandidate(string candidate, (string ImagePath, uint Timestamp, uint ImageSize) key,
        CorDebugDumpReadOperation? operation)
    {
        using FileStream input = DebuggerInputFile.OpenRead(candidate);
        if (!input.CanSeek || input.Length is < 64 or > MaximumImageBytes)
        {
            return null;
        }
        using var image = new PEReader(input, PEStreamOptions.LeaveOpen);
        if (!Matches(image, key.Timestamp, key.ImageSize))
        {
            return null;
        }

        long size = input.Length;
        if (size > MaximumRetainedBytes - _retainedBytes)
        {
            throw new InvalidOperationException("Captured metadata exceeds the 1 GiB retained-image limit.");
        }

        return PublishSnapshot(input, size, key, operation);
    }

    private string PublishSnapshot(Stream input, long size, (string ImagePath, uint Timestamp, uint ImageSize) key,
        CorDebugDumpReadOperation? operation)
    {
        _directory ??= Directory.CreateTempSubdirectory("csls-dump-metadata-").FullName;
        string path = Path.Join(_directory, $"{Guid.NewGuid():N}.dll");
        bool published = false;
        try
        {
            WriteSnapshot(input, path, size, key.Timestamp, key.ImageSize, operation);
            operation?.ThrowIfInterrupted();
            _paths.Add(key, path);
            _retainedBytes += size;
            published = true;
            return path;
        }
        finally
        {
            if (!published)
            {
                File.Delete(path);
            }
        }
    }

    private static void WriteSnapshot(Stream input, string path, long size, uint timestamp, uint imageSize,
        CorDebugDumpReadOperation? operation)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        input.Position = 0;
        byte[] buffer = new byte[64 * 1024];
        long remaining = size;
        while (remaining > 0)
        {
            operation?.ThrowIfInterrupted();
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0)
            {
                throw new EndOfStreamException("The metadata image changed while it was being captured.");
            }
            output.Write(buffer, 0, read);
            remaining -= read;
        }

        output.Position = 0;
        using var image = new PEReader(output, PEStreamOptions.LeaveOpen);
        if (input.Length != size || !Matches(image, timestamp, imageSize))
        {
            throw new InvalidDataException("The copied metadata image does not match the captured PE identity.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (string path in _paths.Values)
        {
            File.Delete(path);
        }
        if (_directory is not null)
        {
            Directory.Delete(_directory);
        }
        _paths.Clear();
        _retainedBytes = 0;
        _disposed = true;
    }

    private static bool Matches(PEReader pe, uint timestamp, uint imageSize)
    {
        if (imageSize == 0 || unchecked((uint)pe.PEHeaders.CoffHeader.TimeDateStamp) != timestamp ||
            pe.PEHeaders.PEHeader?.SizeOfImage != imageSize || !pe.HasMetadata ||
            pe.PEHeaders.MetadataSize is <= 0 or > 64 * 1024 * 1024)
        {
            return false;
        }

        MetadataReader metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != Guid.Empty;
    }

    private static bool IsLocalPath(string path) => Path.IsPathFullyQualified(path) &&
        !path.Contains('\0', StringComparison.Ordinal) && !path.StartsWith("\\\\", StringComparison.Ordinal) &&
        !path.StartsWith("//", StringComparison.Ordinal);
}

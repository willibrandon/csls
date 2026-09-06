using Csls.Debugger.Interop;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Csls.Debugger;

/// <summary>
/// Bridges immutable dump data and exact library resolution into the runtime's offline callbacks.
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class CorDebugDumpCallbacks : ICorDebugDumpDataTarget, ICorDebugDumpLibraryProvider,
    ICorDebugDumpMetadataLocator, IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private const int Failure = unchecked((int)0x80004005);
    private const int Aborted = unchecked((int)0x80004004);
    private readonly ICorDebugDumpSource _source;
    private readonly CorDebugDumpMetadataCache _metadata = new();

    /// <summary>
    /// Gets or sets the request owned by the serialized virtual-process operation.
    /// </summary>
    internal CorDebugDumpReadOperation? Operation { get; set; }

    /// <summary>
    /// Creates callbacks over a caller-owned immutable dump source.
    /// </summary>
    /// <param name="source">The source retained until every virtual-process reference is released.</param>
    internal CorDebugDumpCallbacks(ICorDebugDumpSource source) => _source = source;

    /// <summary>
    /// Gets the last managed callback failure for native activation diagnostics.
    /// </summary>
    internal Exception? LastFailure { get; private set; }

    /// <inheritdoc />
    public int GetMetaData(char* imagePath, uint timestamp, uint imageSize, uint capacity, uint* length, char* path)
    {
        if (length == null)
        {
            return InvalidArgument;
        }

        *length = 0;
        if (imagePath == null || capacity > 32768 || path == null && capacity != 0)
        {
            return InvalidArgument;
        }

        if (Operation is { CanRead: false })
        {
            return Aborted;
        }

        try
        {
            string resolved = _metadata.Resolve(_source, ReadImagePath(imagePath), timestamp, imageSize, Operation);
            uint required = checked((uint)resolved.Length + 1);
            if (required > capacity)
            {
                *length = required;
                return unchecked((int)0x8007007A);
            }

            resolved.AsSpan().CopyTo(new Span<char>(path, resolved.Length));
            path[resolved.Length] = '\0';
            *length = required;
            return 0;
        }
        catch (OperationCanceledException)
        {
            return Aborted;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastFailure = exception;
            return Failure;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _metadata.Dispose();

    /// <inheritdoc />
    public int GetPlatform(int* platform)
    {
        if (platform == null)
        {
            return InvalidArgument;
        }

        try
        {
            bool windows = _source.Platform == OSPlatform.Windows;
            *platform = (windows, _source.Architecture) switch
            {
                (true, Architecture.X86) => 0,
                (true, Architecture.X64) => 1,
                (true, Architecture.Arm) => 5,
                (true, Architecture.Arm64) => 7,
                (false, Architecture.X64) => 8,
                (false, Architecture.X86) => 9,
                (false, Architecture.Arm) => 10,
                (false, Architecture.Arm64) => 11,
                _ => throw new PlatformNotSupportedException("The dump architecture has no supported CoreCLR data-target ABI.")
            };
            return 0;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastFailure = exception;
            return Failure;
        }
    }

    /// <inheritdoc />
    public int ReadVirtual(ulong address, byte* buffer, uint requested, uint* read)
    {
        if (read == null || buffer == null || requested > int.MaxValue)
        {
            return InvalidArgument;
        }

        *read = 0;
        if (Operation is { CanRead: false })
        {
            return Aborted;
        }

        try
        {
            int count = _source.ReadMemory(address, new Span<byte>(buffer, (int)requested));
            if (count < 0 || count > requested)
            {
                return Failure;
            }

            Operation?.RecordMemoryRead(count);
            if (Operation is { CanRead: false })
            {
                return Aborted;
            }

            *read = (uint)count;
            return count > 0 || requested == 0 ? 0 : Failure;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastFailure = exception;
            return Failure;
        }
    }

    /// <inheritdoc />
    public int GetThreadContext(uint threadId, uint flags, uint size, byte* context)
    {
        if (context == null || size > int.MaxValue)
        {
            return InvalidArgument;
        }

        if (Operation is { CanRead: false })
        {
            return Aborted;
        }

        try
        {
            bool available = _source.GetThreadContext(threadId, flags, new Span<byte>(context, (int)size));
            Operation?.RecordContextRead();
            return Operation is { CanRead: false } ? Aborted : available ? 0 : Failure;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastFailure = exception;
            return Failure;
        }
    }

    /// <inheritdoc />
    public int ProvideWindowsLibrary(char* name, char* runtimeModule, int indexType,
        uint timestamp, uint imageSize, nint* path)
    {
        if (name == null || path == null || indexType is not (1 or 2) || runtimeModule != null)
        {
            return InvalidArgument;
        }

        try
        {
            string resolved = _source.ResolveWindowsLibrary(ReadName(name), indexType == 2, timestamp, imageSize);
            *path = AllocatePath(resolved);
            return 0;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastFailure = exception;
            return Failure;
        }
    }

    /// <inheritdoc />
    public int ProvideUnixLibrary(char* name, char* runtimeModule, int indexType,
        byte* buildId, int buildIdSize, nint* path)
    {
        if (name == null || path == null || buildId == null || buildIdSize is <= 0 or > 64 ||
            indexType is not (1 or 2) || runtimeModule != null)
        {
            return InvalidArgument;
        }

        try
        {
            string resolved = _source.ResolveUnixLibrary(ReadName(name), indexType == 2,
                new ReadOnlySpan<byte>(buildId, buildIdSize));
            *path = AllocatePath(resolved);
            return 0;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            LastFailure = exception;
            return Failure;
        }
    }

    private static string ReadImagePath(char* path)
    {
        for (int length = 0; length < 32768; length++)
        {
            if (path[length] == '\0')
            {
                if (length == 0)
                {
                    throw new InvalidDataException("The runtime requested an empty metadata path.");
                }

                return new string(path, 0, length);
            }
        }

        throw new InvalidDataException("The runtime requested an oversized metadata path.");
    }

    private static string ReadName(char* name)
    {
        for (int length = 0; length < 256; length++)
        {
            if (name[length] == '\0')
            {
                string result = new(name, 0, length);
                if (length == 0 || result.Contains('/', StringComparison.Ordinal) || result.Contains('\\', StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The runtime requested an invalid debugger library basename.");
                }

                return result;
            }
        }

        throw new InvalidDataException("The runtime requested an oversized debugger library basename.");
    }

    private static nint AllocatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("The resolved debugger library path must be absolute and contain no null characters.");
        }

        if (OperatingSystem.IsWindows())
        {
            return Marshal.StringToCoTaskMemUni(path);
        }

        nuint bytes = checked((nuint)(path.Length + 1) * sizeof(char));
        char* allocated = (char*)NativeMemory.Alloc(bytes);
        path.AsSpan().CopyTo(new Span<char>(allocated, path.Length));
        allocated[path.Length] = '\0';
        return (nint)allocated;
    }

    private static bool IsRecoverable(Exception exception) => exception is IOException or InvalidOperationException or
        ArgumentException or NotSupportedException or OverflowException or UnauthorizedAccessException or
        InvalidDataException or BadImageFormatException;
}

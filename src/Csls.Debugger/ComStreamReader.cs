using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Copies bounded bytes from a retained native IStream while its runtime is stopped.
/// </summary>
internal static class ComStreamReader
{
    private const int MaximumStreamBytes = 256 * 1024 * 1024;
    private const uint StatFlagNoName = 1;

    /// <summary>
    /// Reads the complete stream from its beginning without retaining its COM pointer.
    /// </summary>
    /// <param name="stream">The borrowed non-null IStream pointer.</param>
    /// <returns>The complete bounded stream content.</returns>
    internal static unsafe byte[] ReadAll(nint stream)
    {
        ArgumentOutOfRangeException.ThrowIfZero(stream);
        nint* vtable = *(nint**)stream;
        var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)vtable[5];
        var stat = (delegate* unmanaged[Stdcall]<nint, ComStorageStatistics*, uint, int>)vtable[12];
        var statistics = new ComStorageStatistics();
        CorDebugHResult.ThrowIfFailed(
            stat(stream, &statistics, StatFlagNoName),
            "IStream.Stat");
        if (statistics._size == 0 || statistics._size > MaximumStreamBytes)
        {
            throw new InvalidDataException(
                $"The in-memory symbol stream size {statistics._size} is outside the supported range of 1 through {MaximumStreamBytes} bytes.");
        }

        ulong position = 0;
        ulong* positionAddress = &position;
        CorDebugHResult.ThrowIfFailed(
            seek(stream, 0, (uint)SeekOrigin.Begin, positionAddress),
            "IStream.Seek");
        position = Volatile.Read(ref *positionAddress);
        if (position != 0)
        {
            throw new InvalidDataException(
                $"The in-memory symbol stream began at unexpected offset {position}.");
        }

        byte[] content = GC.AllocateUninitializedArray<byte>(checked((int)statistics._size));
        fixed (byte* contentAddress = content)
        {
            var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)vtable[3];
            uint offset = 0;
            while (offset < content.Length)
            {
                uint bytesRead = 0;
                uint* bytesReadAddress = &bytesRead;
                uint requested = checked((uint)content.Length - offset);
                CorDebugHResult.ThrowIfFailed(
                    read(stream, contentAddress + offset, requested, bytesReadAddress),
                    "IStream.Read");
                bytesRead = Volatile.Read(ref *bytesReadAddress);
                if (bytesRead == 0 || bytesRead > requested)
                {
                    throw new EndOfStreamException(
                        $"The in-memory symbol stream ended after {offset} of {content.Length} bytes.");
                }

                offset = checked(offset + bytesRead);
            }
        }

        return content;
    }
}

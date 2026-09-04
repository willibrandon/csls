using Csls.Debugger.Contracts;
using Csls.Debugger.Interop;

namespace Csls.Debugger;

/// <summary>
/// Reads bounded target memory through generation-bound managed-value handles.
/// </summary>
internal sealed partial class CorDebugDebuggee
{
    private const int MaximumMemoryReadBytes = 1024 * 1024;

    /// <summary>
    /// Reads a bounded range relative to an opaque managed-array memory handle.
    /// </summary>
    /// <param name="memoryReference">The opaque stopped-state memory handle.</param>
    /// <param name="generation">The current debugger stop generation.</param>
    /// <param name="offset">The signed byte offset from the handle.</param>
    /// <param name="count">The requested byte count.</param>
    /// <returns>The readable prefix and trailing unreadable byte count.</returns>
    internal unsafe DebugMemoryReadResult ReadMemory(
        string memoryReference,
        DebugStopGeneration generation,
        long offset,
        int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memoryReference);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > MaximumMemoryReadBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count),
                $"A memory read cannot exceed {MaximumMemoryReadBytes} bytes.");
        }

        if (!_memoryValues.TryGetValue(memoryReference, out ManagedValueHandle? handle))
        {
            throw new InvalidOperationException(
                $"Memory reference '{memoryReference}' is stale or unknown.");
        }

        ValidateGeneration(handle.Id, handle.Generation, generation);
        ValidateValueLifetime(handle);
        ulong address = AddOffset(handle.MemoryAddress, offset);
        if (count == 0)
        {
            return new DebugMemoryReadResult(
                address,
                ReadOnlyMemory<byte>.Empty,
                UnreadableBytes: 0);
        }

        byte[] buffer = GC.AllocateUninitializedArray<byte>(count);
        nuint bytesRead = 0;
        fixed (byte* bufferAddress = buffer)
        {
            nuint* bytesReadAddress = &bytesRead;
            int result = new ICorDebugProcessAbi(_debugProcess).ReadMemory(
                address,
                checked((uint)count),
                (nint)bufferAddress,
                (nint)bytesReadAddress);
            bytesRead = Math.Min(Volatile.Read(ref *bytesReadAddress), checked((nuint)count));
            if (result < 0 && bytesRead == 0)
            {
                CorDebugHResult.ThrowIfFailed(result, "ICorDebugProcess.ReadMemory");
            }
        }

        int readableCount = checked((int)bytesRead);
        if (readableCount != buffer.Length)
        {
            Array.Resize(ref buffer, readableCount);
        }

        return new DebugMemoryReadResult(address, buffer, count - readableCount);
    }

    private static ulong AddOffset(ulong address, long offset)
    {
        if (offset >= 0)
        {
            return checked(address + (ulong)offset);
        }

        ulong magnitude = offset == long.MinValue
            ? 1UL << 63
            : checked((ulong)-offset);
        return checked(address - magnitude);
    }
}

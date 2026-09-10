using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Captures native ARM64 control and integer registers for a stopped managed thread.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacArm64ThreadContext
{
    private const int ThreadIdentifierInfo = 4;
    private const uint ThreadIdentifierWords = 6;
    private const int ArmThreadState64 = 6;
    private const uint ArmThreadStateWords = 68;
    private const int ContextSize = 912;
    private const uint ControlAndIntegerFlags = 0x00400003;

    /// <summary>
    /// Reads actual native registers and converts them to the public CoreCLR ARM64 context layout.
    /// </summary>
    /// <param name="processId">The stopped managed target.</param>
    /// <param name="threadId">The operating-system thread identifier.</param>
    /// <param name="cancellationToken">Cancels between native thread and register reads.</param>
    /// <returns>The captured control and integer context.</returns>
    internal static unsafe byte[] Read(int processId, int threadId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        using var task = MacTaskHandle.Open(processId);
        cancellationToken.ThrowIfCancellationRequested();
        int result = MacThreadContextNativeMethods.GetThreads(checked((uint)task.DangerousGetHandle()),
            out nint threadArray, out uint count);
        GC.KeepAlive(task);
        if (result != 0)
        {
            throw new InvalidOperationException($"Native thread enumeration failed: Mach status 0x{result:X}.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Span<uint> info = stackalloc uint[checked((int)ThreadIdentifierWords)];
            uint* ports = (uint*)threadArray;
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint words = ThreadIdentifierWords;
                fixed (uint* infoAddress = info)
                {
                    result = MacThreadContextNativeMethods.GetThreadInfo(ports[index], ThreadIdentifierInfo,
                        infoAddress, ref words);
                }

                if (result != 0 || words != ThreadIdentifierWords)
                {
                    throw new InvalidOperationException($"Native thread identity inspection failed: Mach status 0x{result:X}.");
                }

                ulong identity = BinaryPrimitives.ReadUInt64LittleEndian(MemoryMarshal.AsBytes(info));
                if (identity == checked((uint)threadId))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    byte[] context = ReadRegisters(ports[index]);
                    cancellationToken.ThrowIfCancellationRequested();
                    return context;
                }
            }

            throw new InvalidOperationException($"Native thread {threadId} no longer exists in process {processId}.");
        }
        finally
        {
            uint currentTask = MacThreadContextNativeMethods.GetCurrentTask();
            uint* ports = (uint*)threadArray;
            for (uint index = 0; index < count; index++)
            {
                _ = MacThreadContextNativeMethods.ReleasePort(currentTask, ports[index]);
            }

            if (threadArray != 0)
            {
                _ = MacThreadContextNativeMethods.ReleaseMemory(currentTask, checked((ulong)threadArray),
                    (ulong)count * sizeof(uint));
            }
        }
    }

    private static unsafe byte[] ReadRegisters(uint thread)
    {
        Span<uint> state = stackalloc uint[checked((int)ArmThreadStateWords)];
        uint words = ArmThreadStateWords;
        int result;
        fixed (uint* address = state)
        {
            result = MacThreadContextNativeMethods.GetThreadState(thread, ArmThreadState64, address, ref words);
        }

        if (result != 0 || words != ArmThreadStateWords)
        {
            throw new InvalidOperationException($"Native ARM64 register inspection failed: Mach status 0x{result:X}.");
        }

        ReadOnlySpan<byte> captured = MemoryMarshal.AsBytes(state);
        byte[] context = new byte[ContextSize];
        BinaryPrimitives.WriteUInt32LittleEndian(context, ControlAndIntegerFlags);
        // The native state begins with X0 through X28, FP, LR, SP and PC, followed by CPSR.
        captured[..264].CopyTo(context.AsSpan(8));
        captured.Slice(264, sizeof(uint)).CopyTo(context.AsSpan(4));
        return context;
    }
}

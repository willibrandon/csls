using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger;

/// <summary>
/// Captures native Linux general registers under an explicitly owned trace stop.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxThreadContext
{
    private const int Seize = 0x4206;
    private const int Interrupt = 0x4207;
    private const int GetRegisterSet = 0x4204;
    private const int Detach = 17;
    private const int AllThreads = 0x40000000;
    private const int NoSuchProcess = 3;
    private const int Interrupted = 4;

    /// <summary>
    /// Reads an exact native register image and releases the tracing relationship before returning.
    /// </summary>
    /// <param name="threadId">The selected operating-system thread.</param>
    /// <param name="cancellationToken">Cancels before acquisition or after reaching a releasable stop.</param>
    /// <returns>The architecture's complete general-register image.</returns>
    internal static unsafe byte[] ReadRegisters(int threadId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadId);
        cancellationToken.ThrowIfCancellationRequested();
        int length = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => 272,
            Architecture.X64 => 216,
            _ => throw new PlatformNotSupportedException("Native register inspection requires a 64-bit Linux debugger.")
        };
        byte[] registers = new byte[length];
        UnixWaitStatusInterposer.BeginThreadInspection(threadId);
        try
        {
            ThrowIfFailed(LinuxThreadContextNativeMethods.Trace(Seize, threadId, 0, 0), threadId, "PTRACE_SEIZE");
            int resumeSignal = 0;
            try
            {
                ThrowIfFailed(LinuxThreadContextNativeMethods.Trace(Interrupt, threadId, 0, 0), threadId, "PTRACE_INTERRUPT");
                int status = WaitForStop(threadId);
                if ((status & 0xff) != 0x7f)
                {
                    throw new InvalidOperationException($"Native thread {threadId} exited during register inspection.");
                }

                // Interrupt-generated events carry no application signal to reinject.
                resumeSignal = (status >> 16) == 128 ? 0 : (status >> 8) & 0xff;
                cancellationToken.ThrowIfCancellationRequested();
                fixed (byte* address = registers)
                {
                    var vector = new LinuxIoVector { _address = (nint)address, _length = checked((nuint)length) };
                    ThrowIfFailed(LinuxThreadContextNativeMethods.Trace(GetRegisterSet, threadId, 1, (nint)(&vector)),
                        threadId, "PTRACE_GETREGSET");
                    if (vector._length != checked((nuint)length))
                    {
                        throw new InvalidOperationException($"Native thread {threadId} returned an incompatible register image of {vector._length} bytes.");
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                return registers;
            }
            finally
            {
                CLong result = LinuxThreadContextNativeMethods.Trace(Detach, threadId, 0, resumeSignal);
                if (result.Value < 0 && Marshal.GetLastPInvokeError() != NoSuchProcess)
                {
                    ThrowIfFailed(result, threadId, "PTRACE_DETACH");
                }
            }
        }
        finally
        {
            UnixWaitStatusInterposer.EndThreadInspection(threadId);
        }
    }

    /// <summary>
    /// Converts a captured Linux ARM64 register set into the public CoreCLR context layout.
    /// </summary>
    /// <param name="registers">The exact native ARM64 general-register image.</param>
    /// <returns>A context containing captured integer and control registers.</returns>
    internal static byte[] CreateArm64Context(ReadOnlySpan<byte> registers)
    {
        if (registers.Length != 272)
        {
            throw new ArgumentException("An ARM64 register image must contain 272 bytes.", nameof(registers));
        }

        byte[] context = new byte[912];
        BinaryPrimitives.WriteUInt32LittleEndian(context, 0x00400003);
        BinaryPrimitives.WriteUInt32LittleEndian(context.AsSpan(4), BinaryPrimitives.ReadUInt32LittleEndian(registers[264..]));
        registers[..264].CopyTo(context.AsSpan(8));
        return context;
    }

    private static int WaitForStop(int threadId)
    {
        int result;
        int status;
        do
        {
            result = UnixWaitStatusInterposer.WaitProcess(threadId, out status, AllThreads);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == Interrupted);

        if (result != threadId)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Waiting for native thread {threadId} to stop failed.");
        }

        return status;
    }

    private static void ThrowIfFailed(CLong result, int threadId, string operation)
    {
        if (result.Value < 0)
        {
            int error = Marshal.GetLastPInvokeError();
            string guidance = error == 1 && operation == "PTRACE_SEIZE"
                ? " The target must authorize this debugger to trace it and have no other native tracer."
                : string.Empty;
            throw new Win32Exception(error, $"{operation} failed for native thread {threadId}: " +
                new Win32Exception(error).Message + "." + guidance);
        }
    }
}

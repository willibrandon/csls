using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.TestProcessHost;

/// <summary>
/// Exercises actual Windows exception dispatch while an external observer owns the debug-event stream.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsNativeFaultFixture
{
    /// <summary>
    /// Runs a managed exception, terminal native fault, or blocked-process scenario in the owned child.
    /// </summary>
    /// <param name="mode">The managed, bounded, fatal, bounded-fatal, stack-evidence, second-chance, or waiting scenario.</param>
    /// <returns>Zero after the runtime records every requested managed task failure.</returns>
    internal static async Task<int> RunAsync(string mode)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(
            mode is "managed" or "bounded" or "fatal" or "bounded-fatal" or "stack-evidence" or "second-chance" or "waiting", true);
        if (mode == "waiting")
        {
            Console.WriteLine(Environment.ProcessId);
            using var release = new ManualResetEvent(false);
            release.WaitOne();
            return 0;
        }

        int count = mode is "bounded" or "bounded-fatal" ? 12 : mode == "managed" ? 1 : 0;
        for (int index = 0; index < count; index++)
        {
            Task<int> operation = Task.Run(ReadNull);
            Task completion = operation;
            await completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (operation.Exception is not { InnerExceptions: [Exception exception] })
            {
                throw new InvalidOperationException("The native fault must produce exactly one managed task exception.");
            }
            Console.WriteLine(exception.GetType().Name);
        }
        if (mode is "fatal" or "bounded-fatal" or "stack-evidence")
        {
            RaiseAccessViolation(mode == "stack-evidence");
            throw new InvalidOperationException("The terminal native access violation unexpectedly returned.");
        }
        if (mode == "second-chance")
        {
            RaiseSecondChance();
            throw new InvalidOperationException("The second-chance native access violation unexpectedly returned.");
        }
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static unsafe int ReadNull()
    {
        int* pointer = null;
        return *pointer;
    }

    private static unsafe void RaiseAccessViolation(bool announceStorage)
    {
        nuint* arguments = stackalloc nuint[2] { 1, 0x12345678 };
        if (announceStorage)
        {
            Console.WriteLine(FormattableString.Invariant($"arguments=0x{(nuint)arguments:X} bytes={Convert.ToHexString(new ReadOnlySpan<byte>(arguments, 2 * sizeof(nuint)))}"));
        }
        RaiseException(0xc0000005, 0, 2, arguments);
    }

    private static unsafe void RaiseSecondChance()
    {
        byte* storage = stackalloc byte[2048 + 15];
        byte* context = (byte*)(((nuint)storage + 15) & ~(nuint)15);
        new Span<byte>(context, 2048).Clear();
        RtlCaptureContext(context);
        int instructionOffset = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 248,
            Architecture.Arm64 => 264,
            Architecture.X86 => 184,
            _ => throw new PlatformNotSupportedException()
        };
        byte* record = stackalloc byte[152];
        new Span<byte>(record, 152).Clear();
        Unsafe.WriteUnaligned(record, 0xc0000005u);
        Unsafe.WriteUnaligned(record + 4, 1u); // EXCEPTION_NONCONTINUABLE
        Unsafe.WriteUnaligned(record + 8 + sizeof(nint), Unsafe.ReadUnaligned<nuint>(context + instructionOffset));
        Unsafe.WriteUnaligned(record + 8 + 2 * sizeof(nint), 2u);
        int information = IntPtr.Size == 8 ? 32 : 20;
        Unsafe.WriteUnaligned(record + information, (nuint)1);
        Unsafe.WriteUnaligned(record + information + sizeof(nint), (nuint)0x12345678);
        RaiseFailFastException(record, context, 0);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial void RtlCaptureContext(byte* context);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial void RaiseFailFastException(byte* record, byte* context, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe partial void RaiseException(uint code, uint flags, uint count, nuint* arguments);
}

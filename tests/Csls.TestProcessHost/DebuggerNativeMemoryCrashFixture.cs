using System.Globalization;
using System.Runtime.InteropServices;

namespace Csls.TestProcessHost;

/// <summary>
/// Leaves a populated private native allocation available to the runtime's fatal-error dump writer.
/// </summary>
internal static class DebuggerNativeMemoryCrashFixture
{
    /// <summary>
    /// Allocates native memory, announces its address, and terminates the fixture through a fatal runtime error.
    /// </summary>
    /// <returns>A failure code if the fatal runtime operation unexpectedly returns.</returns>
    internal static unsafe int Run()
    {
        const int Length = 1024 * 1024;
        void* memory = NativeMemory.Alloc(Length);
        try
        {
            new Span<byte>(memory, Length).Fill(0x5a);
            Console.Out.WriteLine("native-memory-ready:" + ((nuint)memory).ToString("x", CultureInfo.InvariantCulture));
            Environment.FailFast("native-memory-crash");
            return 1;
        }
        finally
        {
            NativeMemory.Free(memory);
        }
    }
}

using Microsoft.Diagnostics.Runtime;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Records bounded native-register evidence from an owned target after an optimized-value failure.
/// </summary>
internal static class DebuggerNativeContextDiagnostics
{
    private const int MaximumThreads = 32;
    private const int MaximumFrames = 32;
    private const int MaximumReportedFrames = 8;
    private const int Arm64ControlAndIntegerContextSize = 272;

    /// <summary>
    /// Compares the paused fixture's contexts through the native macOS reader while preserving the failing assertion.
    /// </summary>
    /// <param name="testContext">The failing test's diagnostic output.</param>
    /// <param name="processId">The target identifier received from this test's DAP process event.</param>
    internal static void TryWrite(TestContext testContext, int processId)
    {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            return;
        }

        try
        {
            using var target = DataTarget.AttachToProcess(processId, suspend: true);
            using ClrRuntime runtime = target.ClrVersions.Single().CreateRuntime();
            int reportedFrames = 0;
            foreach (ClrThread thread in runtime.Threads.Take(MaximumThreads))
            {
                foreach (ClrStackFrame frame in thread.EnumerateStackTrace(includeContext: true, maxFrames: MaximumFrames))
                {
                    if (frame.Method is not { Type.Name: "Csls.Debugger.Fixtures.CSharp.UnavailableLocalsFixture" } method)
                    {
                        continue;
                    }

                    int bytes = Math.Min(frame.Context.Length, Arm64ControlAndIntegerContextSize);
                    testContext.WriteLine($"Native fixture context: thread={thread.OSThreadId}, " +
                        $"method={method.Name}, SP=0x{frame.StackPointer:X}, PC=0x{frame.InstructionPointer:X}, " +
                        $"context={Convert.ToHexString(frame.Context[..bytes])}");
                    if (++reportedFrames == MaximumReportedFrames)
                    {
                        return;
                    }
                }
            }

            if (reportedFrames == 0)
            {
                testContext.WriteLine("The native reader returned no optimized fixture frames within the diagnostic limits.");
            }
        }
        catch (Exception exception) when (exception is ClrDiagnosticsException or IOException or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            testContext.WriteLine($"Native fixture context capture failed: {exception.GetType().Name}: {exception.Message}");
        }
    }
}

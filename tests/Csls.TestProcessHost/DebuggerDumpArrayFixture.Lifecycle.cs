using System.Diagnostics;

namespace Csls.TestProcessHost;

/// <summary>
/// Retains distinct CLR array layouts while an independent process captures its heap.
/// </summary>
internal static partial class DebuggerDumpArrayFixture
{
    /// <summary>
    /// Ends the target while a debugger-owned function evaluation is running.
    /// </summary>
    internal static void ExitDuringDebuggerEvaluation()
    {
        Console.Error.WriteLine("csls-evaluation-exit-entered");
        Console.Error.Flush();
        Environment.Exit(37);
    }

    /// <summary>
    /// Kills the target without a managed evaluation or process-exit callback.
    /// </summary>
    internal static void CrashDuringDebuggerEvaluation()
    {
        Console.Error.WriteLine("csls-evaluation-crash-entered");
        Console.Error.Flush();
        using var process = Process.GetCurrentProcess();
        process.Kill();
    }
}

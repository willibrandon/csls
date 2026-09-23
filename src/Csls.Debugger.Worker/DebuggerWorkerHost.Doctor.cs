namespace Csls.Debugger.Worker;

/// <summary>
/// Validates debugger runtime components inside the supervised worker process.
/// </summary>
internal static partial class DebuggerWorkerHost
{
    private static int RunDoctor()
    {
        DebuggerEngine.VerifyPlatformSupport();
        Console.Out.WriteLine(
            "The native .NET runtime-debugging components are available.");
        return 0;
    }
}

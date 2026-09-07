namespace Csls.TestProcessHost;

/// <summary>
/// Changes a real target's native inspection permissions through its input stream.
/// </summary>
internal static class DebuggerNativeAuthorizationFixture
{
    /// <summary>
    /// Serves permission commands while preserving the target for managed debugger attachment.
    /// </summary>
    /// <param name="debuggerProcessId">The exact debugger launcher authorized by this fixture.</param>
    /// <returns>Zero after the owner requests normal process exit.</returns>
    internal static int Run(int debuggerProcessId)
    {
        DebuggerNativeAuthorization.AllowDebugger(debuggerProcessId);
        Console.Out.WriteLine("ready");
        Console.Out.Flush();
        while (Console.In.ReadLine() is string command)
        {
            switch (command)
            {
                case "deny":
                    DebuggerNativeAuthorization.SetDumpable(false);
                    Console.Out.WriteLine("denied");
                    break;
                case "allow":
                    DebuggerNativeAuthorization.SetDumpable(true);
                    DebuggerNativeAuthorization.AllowDebugger(debuggerProcessId);
                    Console.Out.WriteLine("allowed");
                    break;
                case "exit":
                    return 0;
                default:
                    throw new InvalidOperationException($"Unknown fixture permission command: {command}.");
            }
            Console.Out.Flush();
        }

        return 0;
    }
}

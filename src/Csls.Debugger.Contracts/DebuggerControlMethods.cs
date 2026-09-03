namespace Csls.Debugger.Contracts;

/// <summary>
/// Names the versioned private debugger control methods.
/// </summary>
public static class DebuggerControlMethods
{
    /// <summary>
    /// Gets the debugger-control protocol version.
    /// </summary>
    public const string GetProtocolVersion = "debugger/getProtocolVersion";

    /// <summary>
    /// Gets the current target session snapshot.
    /// </summary>
    public const string GetSession = "debugger/getSession";

    /// <summary>
    /// Launches a debugger-owned managed target.
    /// </summary>
    public const string Launch = "debugger/launch";

    /// <summary>
    /// Attaches to a running CoreCLR process.
    /// </summary>
    public const string Attach = "debugger/attach";

    /// <summary>
    /// Replaces source breakpoints for one document.
    /// </summary>
    public const string SetSourceBreakpoints = "debugger/setSourceBreakpoints";

    /// <summary>
    /// Pauses the managed target.
    /// </summary>
    public const string Pause = "debugger/pause";

    /// <summary>
    /// Continues the managed target.
    /// </summary>
    public const string Continue = "debugger/continue";

    /// <summary>
    /// Steps one managed thread.
    /// </summary>
    public const string Step = "debugger/step";

    /// <summary>
    /// Gets managed threads at the current stop.
    /// </summary>
    public const string GetThreads = "debugger/getThreads";

    /// <summary>
    /// Gets a managed stack page at the current stop.
    /// </summary>
    public const string GetStack = "debugger/getStack";

    /// <summary>
    /// Gets frame scopes at the current stop.
    /// </summary>
    public const string GetScopes = "debugger/getScopes";

    /// <summary>
    /// Gets a variable page at the current stop.
    /// </summary>
    public const string GetVariables = "debugger/getVariables";

    /// <summary>
    /// Terminates a debugger-owned target.
    /// </summary>
    public const string Terminate = "debugger/terminate";

    /// <summary>
    /// Detaches from a target without terminating it.
    /// </summary>
    public const string Detach = "debugger/detach";
}

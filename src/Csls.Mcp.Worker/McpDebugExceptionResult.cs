namespace Csls.Mcp.Worker;

/// <summary>
/// Returns the exception responsible for one exact stopped generation.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The inspected stop generation.</param>
/// <param name="Exception">The managed exception detail.</param>
internal sealed record McpDebugExceptionResult(
    string DebugSession,
    long StopGeneration,
    McpDebugExceptionInfo Exception) : IMcpDebugSessionResult;

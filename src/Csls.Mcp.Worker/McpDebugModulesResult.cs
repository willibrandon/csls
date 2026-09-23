namespace Csls.Mcp.Worker;

/// <summary>
/// Carries one managed module page for an explicit debugger session.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="Modules">The requested ordered module page.</param>
/// <param name="TotalModules">The complete module count before paging.</param>
internal sealed record McpDebugModulesResult(
    string DebugSession,
    IReadOnlyList<McpDebugModuleInfo> Modules,
    int TotalModules) : IMcpDebugSessionResult;

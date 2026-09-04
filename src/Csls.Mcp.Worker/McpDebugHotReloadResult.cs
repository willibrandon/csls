namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one compiler delta generation committed to an explicit debugger session.
/// </summary>
/// <param name="DebugSession">The explicit debugger-session identifier.</param>
/// <param name="ModuleId">The stable session-local module identifier.</param>
/// <param name="ModuleGeneration">The newly committed module generation.</param>
/// <param name="StopGeneration">The replacement stopped generation.</param>
/// <param name="UpdatedMethods">The aggregate metadata tokens with updated symbols.</param>
internal sealed record McpDebugHotReloadResult(
    string DebugSession,
    int ModuleId,
    int ModuleGeneration,
    long StopGeneration,
    IReadOnlyList<uint> UpdatedMethods) : IMcpDebugSessionResult;

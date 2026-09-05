using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Returns bounded managed-IL disassembly for one exact stop generation.
/// </summary>
/// <param name="DebugSession">The exact debugger-session identifier.</param>
/// <param name="StopGeneration">The inspected stop generation.</param>
/// <param name="Instructions">The ordered instructions and placeholders.</param>
internal sealed record McpDebugDisassemblyResult(
    string DebugSession,
    long StopGeneration,
    IReadOnlyList<DebugInstructionInfo> Instructions) : IMcpDebugSessionResult;

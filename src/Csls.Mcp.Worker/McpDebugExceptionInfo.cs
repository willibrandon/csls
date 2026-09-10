using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP-facing managed exception stop.
/// </summary>
/// <param name="ExceptionId">The fully qualified managed exception type name.</param>
/// <param name="Description">The debugger-readable exception summary.</param>
/// <param name="BreakMode">The exception stage name.</param>
internal sealed record McpDebugExceptionInfo(
    string ExceptionId,
    string Description,
    string BreakMode)
{
    /// <summary>
    /// Projects private debugger exception detail into the MCP contract.
    /// </summary>
    internal static McpDebugExceptionInfo Create(DebugExceptionInfo exception) => new(
        exception.ExceptionId,
        exception.Description,
        exception.BreakMode switch
        {
            DebugExceptionBreakMode.Thrown => "thrown",
            DebugExceptionBreakMode.UserUnhandled => "userUnhandled",
            DebugExceptionBreakMode.Unhandled => "unhandled",
            _ => throw new InvalidDataException(
                $"Unknown debugger exception break mode {exception.BreakMode}.")
        });
}

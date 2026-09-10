using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Describes one MCP managed-exception breakpoint request.
/// </summary>
/// <param name="BreakMode">The stage: thrown, userUnhandled, or unhandled.</param>
/// <param name="ExceptionTypeNames">Exact or base type names; empty selects every type.</param>
internal sealed record McpDebugExceptionBreakpoint(
    string BreakMode,
    IReadOnlyList<string> ExceptionTypeNames)
{
    /// <summary>
    /// Creates the stable MCP projection of one private debugger policy.
    /// </summary>
    /// <param name="breakpoint">The normalized private policy.</param>
    /// <returns>The MCP-facing policy.</returns>
    internal static McpDebugExceptionBreakpoint Create(
        DebugExceptionBreakpointRequest breakpoint) => new(
            breakpoint.BreakMode switch
            {
                DebugExceptionBreakMode.Thrown => "thrown",
                DebugExceptionBreakMode.UserUnhandled => "userUnhandled",
                DebugExceptionBreakMode.Unhandled => "unhandled",
                _ => throw new InvalidDataException(
                    $"Unknown exception break mode {breakpoint.BreakMode}.")
            },
            breakpoint.ExceptionTypeNames);
}

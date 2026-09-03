using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Replaces managed-IL and exception breakpoint sets for MCP agents.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    /// <summary>
    /// Replaces every managed-IL instruction breakpoint at an exact stop.
    /// </summary>
    internal Task<McpDebugInstructionBreakpointsResult> SetInstructionBreakpointsAsync(
        string debugSession,
        long stopGeneration,
        IReadOnlyList<McpDebugInstructionBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ValidateBreakpointCount(breakpoints.Count);
        foreach (McpDebugInstructionBreakpoint breakpoint in breakpoints)
        {
            ValidateRequiredBreakpointText(
                breakpoint.InstructionReference,
                "instructionReference");
            ValidateOptionalBreakpointText(
                breakpoint.Condition,
                "condition",
                MaximumBreakpointExpressionLength);
            ValidateOptionalBreakpointText(breakpoint.HitCondition, "hitCondition");
        }

        McpDebuggerSession session = Resolve(debugSession);
        RequireAgentControl(session);
        return InvokeStoppedAsync(
            session,
            stopGeneration,
            async (selected, client, token) => new McpDebugInstructionBreakpointsResult(
                selected.Id,
                stopGeneration,
                await client.SetInstructionBreakpointsAsync(
                    new DebugInstructionBreakpointSetRequest(
                        breakpoints.Select(static item =>
                            new DebugInstructionBreakpointRequest(
                                item.InstructionReference,
                                item.Offset,
                                item.Condition,
                                item.HitCondition)).ToArray()),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>
    /// Replaces the managed-exception breakpoint policy at an exact stop.
    /// </summary>
    internal Task<McpDebugExceptionBreakpointsResult> SetExceptionBreakpointsAsync(
        string debugSession,
        long stopGeneration,
        IReadOnlyList<McpDebugExceptionBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ValidateBreakpointCount(breakpoints.Count);
        DebugExceptionBreakpointRequest[] requests =
            [.. breakpoints.Select(ConvertExceptionBreakpoint)];
        McpDebuggerSession session = Resolve(debugSession);
        RequireAgentControl(session);
        return InvokeStoppedAsync(
            session,
            stopGeneration,
            async (selected, client, token) =>
            {
                await client.SetExceptionBreakpointsAsync(
                    new DebugExceptionBreakpointSetRequest(requests),
                    token).ConfigureAwait(false);
                return new McpDebugExceptionBreakpointsResult(
                    selected.Id,
                    stopGeneration,
                    breakpoints);
            },
            cancellationToken);
    }

    private static DebugExceptionBreakpointRequest ConvertExceptionBreakpoint(
        McpDebugExceptionBreakpoint breakpoint)
    {
        DebugExceptionBreakMode mode = breakpoint.BreakMode.ToUpperInvariant() switch
        {
            "THROWN" => DebugExceptionBreakMode.Thrown,
            "USERUNHANDLED" => DebugExceptionBreakMode.UserUnhandled,
            "UNHANDLED" => DebugExceptionBreakMode.Unhandled,
            _ => throw InvalidRequest(
                "breakMode must be thrown, userUnhandled, or unhandled.")
        };
        if (breakpoint.ExceptionTypeNames.Count > MaximumBreakpointCount)
        {
            throw InvalidRequest(
                $"Exception type names cannot exceed {MaximumBreakpointCount} entries.");
        }

        foreach (string typeName in breakpoint.ExceptionTypeNames)
        {
            ValidateRequiredBreakpointText(typeName, "exception type name");
        }

        return new DebugExceptionBreakpointRequest(mode, breakpoint.ExceptionTypeNames);
    }
}

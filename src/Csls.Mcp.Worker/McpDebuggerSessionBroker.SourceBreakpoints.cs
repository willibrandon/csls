using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Replaces source and managed-function breakpoint sets for MCP agents.
/// </summary>
internal sealed partial class McpDebuggerSessionBroker
{
    private const int MaximumBreakpointCount = 256;
    private const int MaximumBreakpointTextLength = 1024;
    private const int MaximumBreakpointExpressionLength = 4096;
    private const int MaximumLogMessageLength = 16 * 1024;

    /// <summary>
    /// Replaces every source breakpoint for one document at an exact stop.
    /// </summary>
    internal Task<McpDebugSourceBreakpointsResult> SetSourceBreakpointsAsync(
        string debugSession,
        long stopGeneration,
        string sourcePath,
        IReadOnlyList<McpDebugSourceBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ValidateSourcePath(sourcePath);
        ValidateBreakpointCount(breakpoints.Count);
        foreach (McpDebugSourceBreakpoint breakpoint in breakpoints)
        {
            ValidatePositive(breakpoint.Line, "breakpoint line");
            if (breakpoint.Column is <= 0)
            {
                throw InvalidRequest("breakpoint column must be positive when specified.");
            }

            ValidateOptionalBreakpointText(
                breakpoint.Condition,
                "condition",
                MaximumBreakpointExpressionLength);
            ValidateOptionalBreakpointText(breakpoint.HitCondition, "hitCondition");
            ValidateOptionalBreakpointText(
                breakpoint.LogMessage,
                "logMessage",
                MaximumLogMessageLength);
        }

        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selected, client, token) => new McpDebugSourceBreakpointsResult(
                selected.Id,
                stopGeneration,
                await client.SetSourceBreakpointsAsync(
                    new DebugSourceBreakpointSetRequest(
                        Path.GetFullPath(sourcePath),
                        breakpoints.Select(static item => new DebugSourceBreakpointRequest(
                            item.Line,
                            item.Column,
                            item.Condition,
                            item.HitCondition,
                            item.LogMessage)).ToArray()),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }

    /// <summary>
    /// Replaces every managed-function breakpoint at an exact stop.
    /// </summary>
    internal Task<McpDebugFunctionBreakpointsResult> SetFunctionBreakpointsAsync(
        string debugSession,
        long stopGeneration,
        IReadOnlyList<McpDebugFunctionBreakpoint> breakpoints,
        CancellationToken cancellationToken)
    {
        ValidateBreakpointCount(breakpoints.Count);
        foreach (McpDebugFunctionBreakpoint breakpoint in breakpoints)
        {
            ValidateRequiredBreakpointText(breakpoint.Name, "function name");
            ValidateOptionalBreakpointText(
                breakpoint.Condition,
                "condition",
                MaximumBreakpointExpressionLength);
            ValidateOptionalBreakpointText(breakpoint.HitCondition, "hitCondition");
        }

        McpDebuggerSession session = Resolve(debugSession);
        return InvokeControlledStoppedAsync(
            session,
            stopGeneration,
            async (selected, client, token) => new McpDebugFunctionBreakpointsResult(
                selected.Id,
                stopGeneration,
                await client.SetFunctionBreakpointsAsync(
                    new DebugFunctionBreakpointSetRequest(
                        breakpoints.Select(static item => new DebugFunctionBreakpointRequest(
                            item.Name,
                            item.Condition,
                            item.HitCondition)).ToArray()),
                    token).ConfigureAwait(false)),
            cancellationToken);
    }

    private static void ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            sourcePath.Length > 4096 ||
            !Path.IsPathFullyQualified(sourcePath))
        {
            throw InvalidRequest("sourcePath must be an absolute path of at most 4096 characters.");
        }
    }

    private static void ValidateBreakpointCount(int count)
    {
        if (count > MaximumBreakpointCount)
        {
            throw InvalidRequest(
                $"A breakpoint replacement cannot exceed {MaximumBreakpointCount} entries.");
        }
    }

    private static void ValidateRequiredBreakpointText(
        string value,
        string name,
        int maximumLength = MaximumBreakpointTextLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw InvalidRequest(
                $"{name} must contain between 1 and {maximumLength} characters.");
        }
    }

    private static void ValidateOptionalBreakpointText(
        string? value,
        string name,
        int maximumLength = MaximumBreakpointTextLength)
    {
        if (value is not null)
        {
            ValidateRequiredBreakpointText(value, name, maximumLength);
        }
    }
}

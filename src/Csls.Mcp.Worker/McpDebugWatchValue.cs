using Csls.Debugger.Contracts;

namespace Csls.Mcp.Worker;

/// <summary>
/// Carries one independently evaluated debugger watch expression.
/// </summary>
/// <param name="Expression">The original source-language expression.</param>
/// <param name="Evaluation">The formatted value when evaluation succeeds.</param>
/// <param name="Error">The stable per-expression failure when evaluation fails.</param>
internal sealed record McpDebugWatchValue(
    string Expression,
    DebugEvaluateResult? Evaluation,
    McpDebuggerError? Error);

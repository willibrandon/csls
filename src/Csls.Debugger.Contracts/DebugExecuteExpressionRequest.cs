namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a stopped managed frame and expression for authorized target execution.
/// </summary>
/// <param name="FrameId">The generation-bound managed frame handle.</param>
/// <param name="Expression">The source expression whose target code may execute.</param>
public sealed record DebugExecuteExpressionRequest(
    int FrameId,
    string Expression);

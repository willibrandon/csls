namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a stopped managed frame and expression for evaluation.
/// </summary>
/// <param name="FrameId">The generation-bound managed frame handle.</param>
/// <param name="Expression">The source expression to evaluate.</param>
public sealed record DebugEvaluateRequest(
    int FrameId,
    string Expression);

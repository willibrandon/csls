namespace Csls.Debugger.Contracts;

/// <summary>
/// Defines the operations exposed by one supervised managed evaluator worker.
/// </summary>
public interface IDebuggerEvaluatorTarget
{
    /// <summary>
    /// Compiles source syntax into a versioned language-neutral expression plan.
    /// </summary>
    /// <param name="request">The selected language and expression.</param>
    /// <param name="cancellationToken">Cancels expression binding.</param>
    /// <returns>The validated expression plan.</returns>
    Task<DebugExpressionPlan> CompileAsync(
        DebugExpressionCompileRequest request,
        CancellationToken cancellationToken);
}

using Csls.Debugger.Contracts;

namespace Csls.Debugger.Evaluator.Worker;

/// <summary>
/// Selects compiler-backed source-language expression binders.
/// </summary>
internal sealed class DebuggerEvaluatorTarget : IDebuggerEvaluatorTarget
{
    /// <inheritdoc />
    public Task<DebugExpressionPlan> CompileAsync(
        DebugExpressionCompileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Expression);
        cancellationToken.ThrowIfCancellationRequested();
        DebugExpressionPlan result = request.Language switch
        {
            DebugExpressionLanguage.CSharp => CSharpExpressionLowerer.Bind(request.Expression),
            DebugExpressionLanguage.VisualBasic =>
                VisualBasicExpressionLowerer.Bind(request.Expression),
            _ => throw new NotSupportedException(
                $"The managed evaluator worker does not bind {request.Language} expressions.")
        };
        return Task.FromResult(result);
    }
}

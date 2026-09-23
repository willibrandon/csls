using Csls.Debugger.Contracts;
using Csls.Debugger.Evaluator.FSharp;

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
        return request.Language switch
        {
            DebugExpressionLanguage.CSharp => Task.FromResult(
                CSharpExpressionLowerer.Bind(request.Expression)),
            DebugExpressionLanguage.VisualBasic =>
                Task.FromResult(VisualBasicExpressionLowerer.Bind(request.Expression)),
            DebugExpressionLanguage.FSharp => FSharpExpressionLowerer.BindAsync(
                request.Expression,
                cancellationToken),
            _ => throw new NotSupportedException(
                $"The managed evaluator worker does not bind {request.Language} expressions.")
        };
    }
}

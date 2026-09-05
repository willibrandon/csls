using Csls.Debugger.Contracts;
using StreamJsonRpc;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Registers the evaluator method table without reflection scanning.
/// </summary>
internal static class DebuggerEvaluatorMethodRegistry
{
    /// <summary>
    /// Registers every private evaluator operation.
    /// </summary>
    /// <param name="rpc">The evaluator server connection.</param>
    /// <param name="target">The compiler-backed evaluator target.</param>
    internal static void Register(JsonRpc rpc, IDebuggerEvaluatorTarget target)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        ArgumentNullException.ThrowIfNull(target);
        rpc.AddLocalRpcMethod(
            DebuggerEvaluatorMethods.GetProtocolVersion,
            new Func<int>(static () => DebuggerEvaluatorProtocol.CurrentVersion));
        var attribute = new JsonRpcMethodAttribute(DebuggerEvaluatorMethods.Compile)
        {
            UseSingleObjectParameterDeserialization = true
        };
        Delegate handler = new Func<DebugExpressionCompileRequest, CancellationToken,
            Task<DebugExpressionPlan>>(target.CompileAsync);
        rpc.AddLocalRpcMethod(
            handler.Method,
            handler.Target ?? throw new InvalidOperationException(
                "The evaluator compile method requires an instance target."),
            attribute);
    }
}

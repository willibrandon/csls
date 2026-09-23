using StreamJsonRpc;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Creates the NativeAOT-safe formatter for private evaluator RPC.
/// </summary>
internal static class DebuggerEvaluatorRpcFormatter
{
    /// <summary>
    /// Creates a formatter owned by the caller.
    /// </summary>
    /// <returns>The private evaluator formatter.</returns>
    internal static NerdbankMessagePackFormatter Create() => new()
    {
        TypeShapeProvider = DebuggerEvaluatorClient.GeneratedTypeShapeProvider
    };
}

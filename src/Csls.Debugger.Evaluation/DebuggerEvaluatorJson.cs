using StreamJsonRpc;
using System.Text.Json;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Creates source-generated JSON configuration for evaluator RPC.
/// </summary>
internal static class DebuggerEvaluatorJson
{
    /// <summary>
    /// Creates an evaluator formatter owned by the caller.
    /// </summary>
    /// <returns>The source-generated evaluator formatter.</returns>
    internal static SystemTextJsonFormatter CreateFormatter() => new()
    {
        JsonSerializerOptions = new JsonSerializerOptions(
            DebuggerEvaluatorJsonSerializerContext.Default.Options)
        {
            TypeInfoResolver = DebuggerEvaluatorJsonSerializerContext.Default
        }
    };
}

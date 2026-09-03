using Csls.Debugger.Contracts;
using StreamJsonRpc.Protocol;
using System.Text.Json.Serialization;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Provides NativeAOT-safe JSON metadata for evaluator RPC payloads.
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DebugExpressionCompileRequest))]
[JsonSerializable(typeof(DebugExpressionLanguage))]
[JsonSerializable(typeof(DebugExpressionNode))]
[JsonSerializable(typeof(DebugExpressionNodeKind))]
[JsonSerializable(typeof(DebugExpressionOperator))]
[JsonSerializable(typeof(DebugExpressionPlan))]
[JsonSerializable(typeof(IReadOnlyList<DebugExpressionNode>))]
[JsonSerializable(typeof(CommonErrorData))]
internal sealed partial class DebuggerEvaluatorJsonSerializerContext : JsonSerializerContext;

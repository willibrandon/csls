using Csls.Debugger.Contracts;
using PolyType;
using StreamJsonRpc.Protocol;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Roots generated type shapes for every private evaluator RPC payload.
/// </summary>
[GenerateShapeFor<int>]
[GenerateShapeFor<CommonErrorData>]
[GenerateShapeFor<DebugExpressionCompileRequest>]
[GenerateShapeFor<DebugExpressionLanguage>]
[GenerateShapeFor<DebugExpressionNode>]
[GenerateShapeFor<DebugExpressionNodeKind>]
[GenerateShapeFor<DebugExpressionOperator>]
[GenerateShapeFor<DebugExpressionPlan>]
[GenerateShapeFor<IReadOnlyList<DebugExpressionNode>>]
internal sealed partial class DebuggerEvaluatorClient;

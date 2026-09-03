namespace Csls.Debugger.Contracts;

/// <summary>
/// Carries one immutable operation in a language-neutral debugger expression tree.
/// </summary>
/// <param name="Kind">The node operation.</param>
/// <param name="Operator">The normalized operator when the node applies one.</param>
/// <param name="Text">The identifier, member name, or decoded literal text when applicable.</param>
/// <param name="TypeName">The compiler-known literal type, or null when runtime binding is required.</param>
/// <param name="Children">The ordered operand nodes.</param>
public sealed record DebugExpressionNode(
    DebugExpressionNodeKind Kind,
    DebugExpressionOperator Operator,
    string? Text,
    string? TypeName,
    IReadOnlyList<DebugExpressionNode> Children);

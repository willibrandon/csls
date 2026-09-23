namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects a source-language grammar and expression for evaluator binding.
/// </summary>
/// <param name="Language">The frame source language.</param>
/// <param name="Expression">The source expression to bind.</param>
public sealed record DebugExpressionCompileRequest(
    DebugExpressionLanguage Language,
    string Expression);

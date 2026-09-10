using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Proves non-null conditional-access receivers through guarded, unchanged local projections.
/// </summary>
internal static class ConditionalAccessGuardProof
{
    /// <summary>
    /// Checks whether an exiting guard establishes that a later conditional-access receiver exists.
    /// </summary>
    internal static bool HasNonNullReceiver(ConditionalAccessExpressionSyntax access, SyntaxNodeAnalysisContext context)
    {
        ISymbol? receiver = GetStableVariable(access.Expression, context);
        if (receiver is null)
        {
            return false;
        }

        SyntaxNode current = access;
        while (current.Parent is not null)
        {
            if (current.Parent is BlockSyntax block && current is StatementSyntax statement &&
                block.Statements.Take(block.Statements.IndexOf(statement)).OfType<IfStatementSyntax>()
                    .Any(guard => guard.Else is null && AlwaysExits(guard.Statement) &&
                        GetGuardedExpression(guard.Condition) is ExpressionSyntax guarded &&
                        ImpliesReceiver(guarded, receiver, block, guard, statement, context)))
            {
                return true;
            }

            if (current.Parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or
                BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
            {
                return false;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool ImpliesReceiver(ExpressionSyntax expression, ISymbol receiver, BlockSyntax block,
        StatementSyntax guard, StatementSyntax current, SyntaxNodeAnalysisContext context)
    {
        StatementSyntax first = guard;
        for (int depth = 0; depth < 64; depth++)
        {
            expression = Unwrap(expression);
            if (expression is ConditionalAccessExpressionSyntax conditional)
            {
                expression = conditional.Expression;
                continue;
            }

            ISymbol? variable = GetStableVariable(expression, context);
            if (variable is null || !IsUnchanged(variable, block, first, current, context))
            {
                return false;
            }

            if (SymbolEqualityComparer.Default.Equals(variable, receiver))
            {
                return true;
            }

            if (variable is not ILocalSymbol local ||
                local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not
                    VariableDeclaratorSyntax { Initializer.Value: { } initializer } declarator ||
                declarator.Parent?.Parent is not LocalDeclarationStatementSyntax declaration ||
                declaration.Parent != block || declaration.Declaration.Variables.Count != 1 ||
                declaration.SpanStart >= first.SpanStart ||
                context.SemanticModel.GetConversion(initializer, context.CancellationToken).IsUserDefined)
            {
                return false;
            }

            first = block.Statements[block.Statements.IndexOf(declaration) + 1];
            DataFlowAnalysis? initialFlow = context.SemanticModel.AnalyzeDataFlow(initializer);
            if (initialFlow is null || !initialFlow.Succeeded || initialFlow.WrittenInside.Length != 0 ||
                !IsUnchanged(variable, block, first, current, context))
            {
                return false;
            }

            expression = initializer;
        }

        return false;
    }

    private static bool IsUnchanged(ISymbol variable, BlockSyntax block, StatementSyntax first,
        StatementSyntax current, SyntaxNodeAnalysisContext context)
    {
        DataFlowAnalysis? scope = context.SemanticModel.AnalyzeDataFlow(block);
        DataFlowAnalysis? region = context.SemanticModel.AnalyzeDataFlow(first, current);
        return scope is { Succeeded: true } && region is { Succeeded: true } &&
            !scope.Captured.Contains(variable, SymbolEqualityComparer.Default) &&
            !scope.UnsafeAddressTaken.Contains(variable, SymbolEqualityComparer.Default) &&
            !region.WrittenInside.Contains(variable, SymbolEqualityComparer.Default);
    }

    private static ISymbol? GetStableVariable(ExpressionSyntax expression, SyntaxNodeAnalysisContext context) =>
        context.SemanticModel.GetSymbolInfo(Unwrap(expression), context.CancellationToken).Symbol switch
        {
            ILocalSymbol { RefKind: RefKind.None } local => local,
            IParameterSymbol { RefKind: RefKind.None } parameter => parameter,
            _ => null
        };

    private static ExpressionSyntax? GetGuardedExpression(ExpressionSyntax condition)
    {
        if (Unwrap(condition) is not IsPatternExpressionSyntax pattern)
        {
            return null;
        }

        return pattern.Pattern switch
        {
            ConstantPatternSyntax { Expression.RawKind: (int)SyntaxKind.NullLiteralExpression } => pattern.Expression,
            UnaryPatternSyntax
            {
                RawKind: (int)SyntaxKind.NotPattern,
                Pattern: DeclarationPatternSyntax or TypePatternSyntax or RecursivePatternSyntax
            } => pattern.Expression,
            _ => null
        };
    }

    private static bool AlwaysExits(StatementSyntax statement) =>
        statement is ReturnStatementSyntax or ThrowStatementSyntax ||
        statement is BlockSyntax { Statements.Count: > 0 } block && AlwaysExits(block.Statements[block.Statements.Count - 1]);

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parentheses)
        {
            expression = parentheses.Expression;
        }

        return expression;
    }
}

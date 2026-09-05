using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Recognizes throwing operations before a constructed disposable reaches cleanup or tuple transfer.
/// </summary>
internal static class DisposableLocalOwnership
{
    /// <summary>
    /// Determines whether a local can leak before entering its cleanup region or returning a tuple.
    /// </summary>
    /// <param name="local">The constructed local resource.</param>
    /// <param name="variable">The local resource declaration.</param>
    /// <param name="declaration">The containing local declaration statement.</param>
    /// <param name="block">The enclosing executable block.</param>
    /// <param name="context">The analyzer's semantic context.</param>
    /// <returns>Whether an operation can throw while the local has no exception cleanup.</returns>
    internal static bool HasUnprotectedTransfer(
        ILocalSymbol local,
        VariableDeclaratorSyntax variable,
        LocalDeclarationStatementSyntax declaration,
        BlockSyntax block,
        SyntaxNodeAnalysisContext context)
    {
        if (!local.Type.AllInterfaces.Any(static type =>
                type.ToDisplayString() is "System.IDisposable" or "System.IAsyncDisposable"))
        {
            return false;
        }

        bool mayThrow = declaration.Declaration.Variables
            .SkipWhile(candidate => candidate != variable)
            .Skip(1)
            .Any(candidate => MayThrow(candidate, context));
        foreach (StatementSyntax statement in block.Statements
            .SkipWhile(candidate => candidate != declaration)
            .Skip(1))
        {
            if (statement is TryStatementSyntax protection &&
                HasExceptionCleanup(protection, local, context))
            {
                return mayThrow;
            }

            mayThrow |= MayThrow(statement, context);
            if (ReturnsTupleElement(statement, local, context))
            {
                return mayThrow;
            }

            if (DisposesLocal(statement, local, context))
            {
                return false;
            }
        }

        return false;
    }

    private static bool HasExceptionCleanup(
        TryStatementSyntax statement,
        ILocalSymbol local,
        SyntaxNodeAnalysisContext context)
    {
        if (statement.Finally is { } cleanup && DisposesLocal(cleanup.Block, local, context))
        {
            return true;
        }

        return statement.Catches.Any(clause =>
            clause.Filter is null &&
            (clause.Declaration is null || context.SemanticModel.GetTypeInfo(
                clause.Declaration.Type, context.CancellationToken).Type?.ToDisplayString() ==
                "System.Exception") &&
            DisposesLocal(clause.Block, local, context));
    }

    private static bool ReturnsTupleElement(
        StatementSyntax statement,
        ILocalSymbol local,
        SyntaxNodeAnalysisContext context) =>
        statement.DescendantNodesAndSelf(DescendIntoExecution)
            .OfType<ReturnStatementSyntax>()
            .Any(returned => returned.Expression is TupleExpressionSyntax tuple &&
                tuple.Arguments.Any(argument => argument.Expression is IdentifierNameSyntax identifier &&
                    IsLocal(identifier, local, context)));

    private static bool DisposesLocal(
        SyntaxNode scope,
        ILocalSymbol local,
        SyntaxNodeAnalysisContext context) =>
        scope.DescendantNodesAndSelf(DescendIntoExecution)
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.ArgumentList.Arguments.Count == 0 &&
                invocation.Expression is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax receiver,
                    Name.Identifier.ValueText: "Dispose" or "DisposeAsync"
                } && IsLocal(receiver, local, context));

    private static bool MayThrow(SyntaxNode scope, SyntaxNodeAnalysisContext context) =>
        scope.DescendantNodesAndSelf(DescendIntoExecution).Any(node =>
            node is BaseObjectCreationExpressionSyntax or InvocationExpressionSyntax or
                AwaitExpressionSyntax or ThrowStatementSyntax or ThrowExpressionSyntax or
                ElementAccessExpressionSyntax or CastExpressionSyntax ||
            node is MemberAccessExpressionSyntax member &&
                context.SemanticModel.GetSymbolInfo(member, context.CancellationToken).Symbol is
                    IPropertySymbol);

    private static bool IsLocal(
        IdentifierNameSyntax identifier,
        ILocalSymbol local,
        SyntaxNodeAnalysisContext context) =>
        SymbolEqualityComparer.Default.Equals(local,
            context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol);

    private static bool DescendIntoExecution(SyntaxNode node) =>
        node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax);
}

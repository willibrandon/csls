using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Proves null correlations for static factories with a null-return guard and a constructed reference result.
/// </summary>
internal static class GuardedFactoryNullProof
{
    /// <summary>
    /// Determines a factory result's nullness for the caller's proven argument state without assuming annotations.
    /// </summary>
    /// <param name="invocation">The local's factory initializer.</param>
    /// <param name="source">The unchanged local or parameter passed to the factory.</param>
    /// <param name="destination">The local's declared storage type.</param>
    /// <param name="sourceIsNull">The argument state required by the short-circuit guard.</param>
    /// <param name="context">The caller's semantic analysis and cancellation context.</param>
    /// <param name="returnsNull">The proven null state of the selected return.</param>
    /// <returns>Whether both factory branches and their implicit conversions establish the correlation.</returns>
    internal static bool TryGetNullValue(InvocationExpressionSyntax invocation, ISymbol source,
        ITypeSymbol destination, bool sourceIsNull, SyntaxNodeAnalysisContext context, out bool returnsNull)
    {
        returnsNull = false;
        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not
            IMethodSymbol { IsStatic: true, IsGenericMethod: false, ReturnsByRef: false, ReturnsByRefReadonly: false } method ||
            method.Parameters.Length != 1 || method.Parameters[0].RefKind != RefKind.None ||
            invocation.ArgumentList.Arguments.Count != 1 ||
            !SymbolEqualityComparer.Default.Equals(source, context.SemanticModel.GetSymbolInfo(
                invocation.ArgumentList.Arguments[0].Expression, context.CancellationToken).Symbol) ||
            context.SemanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression,
                context.CancellationToken).Type is not ITypeSymbol argumentType ||
            !IsReferenceConversion(context.Compilation, argumentType, method.Parameters[0].Type) ||
            !IsReferenceConversion(context.Compilation, method.ReturnType, destination) ||
            method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is not
                MethodDeclarationSyntax { Body: BlockSyntax body } || body.Statements.Count < 2 ||
            body.Statements[0] is not IfStatementSyntax { Else: null } guard ||
            guard.Condition is not IsPatternExpressionSyntax
            {
                Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax nullPattern }
            } condition || !nullPattern.IsKind(SyntaxKind.NullLiteralExpression) ||
            GetReturn(guard.Statement)?.Expression is not LiteralExpressionSyntax nullReturn ||
            !nullReturn.IsKind(SyntaxKind.NullLiteralExpression) ||
            body.Statements[body.Statements.Count - 1] is not ReturnStatementSyntax { Expression: ExpressionSyntax created } ||
            created is not (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax))
        {
            return false;
        }

        SemanticModel factoryModel = context.Compilation.GetSemanticModel(body.SyntaxTree);
        if (!SymbolEqualityComparer.Default.Equals(method.Parameters[0],
                factoryModel.GetSymbolInfo(condition.Expression, context.CancellationToken).Symbol) ||
            factoryModel.GetTypeInfo(created, context.CancellationToken).Type is not ITypeSymbol createdType ||
            !IsReferenceConversion(context.Compilation, createdType, method.ReturnType) ||
            body.DescendantNodes(static node => node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .OfType<ReturnStatementSyntax>().Count() != 2)
        {
            return false;
        }

        returnsNull = sourceIsNull;
        return true;
    }

    private static ReturnStatementSyntax? GetReturn(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax result => result,
        BlockSyntax { Statements.Count: 1 } block => block.Statements[0] as ReturnStatementSyntax,
        _ => null
    };

    private static bool IsReferenceConversion(Compilation compilation, ITypeSymbol source, ITypeSymbol destination) =>
        source.IsReferenceType && destination.IsReferenceType &&
        compilation.ClassifyCommonConversion(source, destination) is { IsIdentity: true } or { IsReference: true };
}

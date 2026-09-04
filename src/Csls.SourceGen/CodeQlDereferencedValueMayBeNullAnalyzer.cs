using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents nullable out variables from being force-dereferenced outside their guard.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlDereferencedValueMayBeNullAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a nullable out variable dereference that requires explicit flow proof.
    /// </summary>
    public const string DiagnosticId = "CSLS0015";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Prove nullable out variables before dereferencing",
        "Nullable out variable '{0}' is force-dereferenced outside its declaring guard",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nullable out variables must not introduce CodeQL cs/dereferenced-value-may-be-null findings.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            AnalyzeSuppression,
            SyntaxKind.SuppressNullableWarningExpression);
    }

    private static void AnalyzeSuppression(SyntaxNodeAnalysisContext context)
    {
        var suppression = (PostfixUnaryExpressionSyntax)context.Node;
        if (suppression.Operand is not IdentifierNameSyntax identifier ||
            context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol is not
                ILocalSymbol local ||
            local.NullableAnnotation != NullableAnnotation.Annotated ||
            local.DeclaringSyntaxReferences
                .Select(reference => reference.GetSyntax(context.CancellationToken))
                .OfType<SingleVariableDesignationSyntax>()
                .FirstOrDefault(IsOutVariable) is null ||
            IsProtectedByExplicitNullGuard(suppression, local, context))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            suppression.GetLocation(),
            local.Name));
    }

    private static bool IsOutVariable(SingleVariableDesignationSyntax declaration) =>
        declaration.FirstAncestorOrSelf<ArgumentSyntax>()?.RefOrOutKeyword.IsKind(
            SyntaxKind.OutKeyword) == true;

    private static bool IsProtectedByExplicitNullGuard(
        PostfixUnaryExpressionSyntax suppression,
        ILocalSymbol local,
        SyntaxNodeAnalysisContext context) =>
        suppression.Ancestors()
            .OfType<IfStatementSyntax>()
            .Any(statement =>
                statement.Statement.Span.Contains(suppression.Span) &&
                statement.Condition.DescendantNodesAndSelf()
                    .OfType<IsPatternExpressionSyntax>()
                    .Any(expression =>
                        expression.Pattern is UnaryPatternSyntax
                        {
                            RawKind: (int)SyntaxKind.NotPattern,
                            Pattern: ConstantPatternSyntax
                            {
                                Expression.RawKind: (int)SyntaxKind.NullLiteralExpression,
                            },
                        } &&
                        SymbolEquals(expression.Expression, local, context)));

    private static bool SymbolEquals(
        ExpressionSyntax expression,
        ISymbol symbol,
        SyntaxNodeAnalysisContext context) =>
        SymbolEqualityComparer.Default.Equals(
            context.SemanticModel.GetSymbolInfo(expression, context.CancellationToken).Symbol,
            symbol);
}

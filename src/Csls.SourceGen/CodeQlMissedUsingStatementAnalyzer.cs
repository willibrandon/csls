using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents manual local-resource disposal that CodeQL requires using syntax for.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlMissedUsingStatementAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies disposable-local cleanup that must use structured ownership.
    /// </summary>
    public const string DiagnosticId = "CSLS0006";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Use structured disposable ownership",
        "Disposable local '{0}' is manually disposed in a finally block",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Disposable locals must not introduce CodeQL cs/missed-using-statement findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation is not
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "Dispose"
                }
            } ||
            !IsInsideFinally(invocation))
        {
            return;
        }

        var member = (MemberAccessExpressionSyntax)invocation.Expression;
        ExpressionSyntax receiver = StripCasts(member.Expression);
        if (context.SemanticModel.GetSymbolInfo(receiver, context.CancellationToken).Symbol is not ILocalSymbol local ||
            context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method ||
            !IsDisposeMethod(method, context.Compilation))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            receiver.GetLocation(),
            local.Name));
    }

    private static bool IsDisposeMethod(IMethodSymbol method, Compilation compilation)
    {
        INamedTypeSymbol? disposable = compilation.GetTypeByMetadataName("System.IDisposable");
        IMethodSymbol? dispose = disposable?.GetMembers("Dispose").OfType<IMethodSymbol>().SingleOrDefault();
        if (dispose is null || method.IsStatic)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, dispose) ||
            SymbolEqualityComparer.Default.Equals(method.OriginalDefinition,
                method.ContainingType.FindImplementationForInterfaceMember(dispose)?.OriginalDefinition);
    }

    private static bool IsInsideFinally(SyntaxNode node)
    {
        for (SyntaxNode? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is FinallyClauseSyntax)
            {
                return true;
            }

            if (parent is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                return false;
            }
        }

        return false;
    }

    private static ExpressionSyntax StripCasts(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized:
                    expression = parenthesized.Expression;
                    break;
                case CastExpressionSyntax cast:
                    expression = cast.Expression;
                    break;
                default:
                    return expression;
            }
        }
    }
}

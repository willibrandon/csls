using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Requires explicit cleanup for disposable locals and exception-safe ownership transfers.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlLocalDisposableAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a disposable local whose cleanup is indirect or whose transfer is unprotected.
    /// </summary>
    public const string DiagnosticId = "CSLS0008";

    private const string DisposableCollectionTypeName =
        "Csls.Debugger.DisposableCollection<T>";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Protect disposable local ownership",
        "Disposable local '{0}' requires explicit, exception-safe cleanup or ownership transfer",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Transferred locals must not introduce CodeQL cs/local-not-disposed or cs/dispose-not-called-on-throw findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeDeclaration, SyntaxKind.LocalDeclarationStatement);
    }

    private static void AnalyzeDeclaration(SyntaxNodeAnalysisContext context)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;
        if (!declaration.UsingKeyword.IsKind(SyntaxKind.None) ||
            declaration.Parent is not BlockSyntax block)
        {
            return;
        }

        foreach (VariableDeclaratorSyntax variable in declaration.Declaration.Variables)
        {
            if (variable.Initializer?.Value is not { } initializer || !CreatesResource(initializer) ||
                context.SemanticModel.GetDeclaredSymbol(variable, context.CancellationToken) is not
                    ILocalSymbol local ||
                !(DisposableLocalOwnership.HasUnprotectedTransfer(
                    local, variable, declaration, block, context) ||
                    initializer is (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax) &&
                    HasConfiguredLibraryDisposal(local, block, context)))
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(s_rule, variable.GetLocation(), local.Name));
        }
    }

    private static bool CreatesResource(ExpressionSyntax expression) => expression switch
    {
        ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or InvocationExpressionSyntax => true,
        ConditionalExpressionSyntax conditional => CreatesResource(conditional.WhenTrue) || CreatesResource(conditional.WhenFalse),
        ParenthesizedExpressionSyntax parenthesized => CreatesResource(parenthesized.Expression),
        _ => false
    };

    private static bool HasConfiguredLibraryDisposal(
        ILocalSymbol local,
        BlockSyntax block,
        SyntaxNodeAnalysisContext context)
    {
        if (!local.Type.DeclaringSyntaxReferences.IsEmpty ||
            local.Type.ContainingAssembly?.Name.StartsWith("Csls.", StringComparison.Ordinal) == true ||
            !local.Type.AllInterfaces.Any(static type => type.ToDisplayString() == "System.IDisposable"))
        {
            return false;
        }

        return block.DescendantNodes(static node =>
                node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<InvocationExpressionSyntax>()
            .Where(IsAsyncUsingExpression)
            .Any(invocation => invocation is
            {
                Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver }
            } &&
                SymbolEqualityComparer.Default.Equals(local,
                    context.SemanticModel.GetSymbolInfo(receiver, context.CancellationToken).Symbol) &&
                context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol
                {
                    Name: "ConfigureAwait",
                    ReturnType: { } returnType,
                    ContainingType: { } containingType
                } &&
                returnType.ToDisplayString() == "System.Runtime.CompilerServices.ConfiguredAsyncDisposable" &&
                containingType.ToDisplayString() == "System.Threading.Tasks.TaskAsyncEnumerableExtensions");
    }

    private static bool IsAsyncUsingExpression(InvocationExpressionSyntax invocation) =>
        invocation.Parent switch
        {
            UsingStatementSyntax statement => !statement.AwaitKeyword.IsKind(SyntaxKind.None),
            EqualsValueClauseSyntax
            {
                Parent: VariableDeclaratorSyntax
                {
                    Parent: VariableDeclarationSyntax { Parent: LocalDeclarationStatementSyntax declaration }
                }
            } => !declaration.AwaitKeyword.IsKind(SyntaxKind.None),
            _ => false
        };

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (context.SemanticModel.GetSymbolInfo(
                invocation,
                context.CancellationToken).Symbol is not IMethodSymbol
                {
                    Name: "Acquire"
                } method ||
            method.ContainingType.OriginalDefinition.ToDisplayString() !=
                DisposableCollectionTypeName ||
            invocation.ArgumentList.Arguments.Count != 1 ||
            GetLambdaValue(invocation.ArgumentList.Arguments[0].Expression) is not
                IdentifierNameSyntax identifier ||
            context.SemanticModel.GetSymbolInfo(
                identifier,
                context.CancellationToken).Symbol is not ILocalSymbol local ||
            !local.Type.AllInterfaces.Any(static item =>
                item.ToDisplayString() == "System.IDisposable") ||
            local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(
                context.CancellationToken) is not VariableDeclaratorSyntax variable ||
            variable.Initializer?.Value is not
                (ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax) ||
            variable.Parent?.Parent is not LocalDeclarationStatementSyntax declaration ||
            !declaration.UsingKeyword.IsKind(SyntaxKind.None))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            variable.GetLocation(),
            local.Name));
    }

    private static ExpressionSyntax? GetLambdaValue(ExpressionSyntax expression) => expression switch
    {
        ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } value } => value,
        SimpleLambdaExpressionSyntax { ExpressionBody: { } value } => value,
        _ => null
    };
}

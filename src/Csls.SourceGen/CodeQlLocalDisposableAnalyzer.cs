using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents unscoped local disposables transferred into repository ownership helpers.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlLocalDisposableAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a transferred disposable local that requires a using declaration.
    /// </summary>
    public const string DiagnosticId = "CSLS0008";

    private const string DisposableCollectionTypeName =
        "Csls.Debugger.DisposableCollection<T>";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Scope transferred local disposables",
        "Disposable local '{0}' must be declared with using before ownership transfer",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Transferred locals must not introduce CodeQL cs/local-not-disposed findings.");

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

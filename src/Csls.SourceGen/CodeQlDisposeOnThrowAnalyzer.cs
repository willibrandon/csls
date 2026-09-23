using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents disposable collection lifetimes that CodeQL cannot prove exception-safe.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlDisposeOnThrowAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a disposable collection local that requires a using declaration.
    /// </summary>
    public const string DiagnosticId = "CSLS0007";

    private const string DisposableCollectionTypeName =
        "Csls.Debugger.DisposableCollection<T>";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Use an exception-safe disposable collection lifetime",
        "Disposable collection '{0}' must be declared with using",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Disposable collections must not introduce CodeQL cs/dispose-not-called-on-throw findings.");

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
            AnalyzeLocalDeclaration,
            SyntaxKind.LocalDeclarationStatement);
    }

    private static void AnalyzeLocalDeclaration(SyntaxNodeAnalysisContext context)
    {
        var declaration = (LocalDeclarationStatementSyntax)context.Node;
        if (!declaration.UsingKeyword.IsKind(SyntaxKind.None))
        {
            return;
        }

        foreach (VariableDeclaratorSyntax variable in declaration.Declaration.Variables)
        {
            if (variable.Initializer?.Value is not ExpressionSyntax initializer ||
                context.SemanticModel.GetTypeInfo(
                    initializer,
                    context.CancellationToken).Type?.OriginalDefinition.ToDisplayString() !=
                    DisposableCollectionTypeName)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                s_rule,
                variable.GetLocation(),
                variable.Identifier.ValueText));
        }
    }
}

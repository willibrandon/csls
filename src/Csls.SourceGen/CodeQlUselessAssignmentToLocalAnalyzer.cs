using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents local writes that are never observed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlUselessAssignmentToLocalAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a local assignment whose value does not flow to a read.
    /// </summary>
    public const string DiagnosticId = "CSLS0018";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Remove useless local assignment",
        "Assigned value for local '{0}' is never observed",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Dead local writes must not introduce CodeQL cs/useless-assignment-to-local findings.");

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
            AnalyzeAssignment,
            SyntaxKind.SimpleAssignmentExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeVariable,
            SyntaxKind.VariableDeclarator);
    }

    private static void AnalyzeAssignment(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not IdentifierNameSyntax identifier ||
            assignment.Parent is not ExpressionStatementSyntax statement ||
            context.SemanticModel.GetSymbolInfo(
                identifier,
                context.CancellationToken).Symbol is not ILocalSymbol local ||
            !context.SemanticModel.GetConstantValue(
                assignment.Right,
                context.CancellationToken).HasValue)
        {
            return;
        }

        DataFlowAnalysis? flow = context.SemanticModel.AnalyzeDataFlow(statement);
        if (flow is null || !flow.Succeeded || FlowsOut(flow, local))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            assignment.GetLocation(),
            local.Name));
    }

    private static bool FlowsOut(DataFlowAnalysis flow, ILocalSymbol local)
    {
        foreach (ISymbol symbol in flow.DataFlowsOut.Where(symbol =>
            SymbolEqualityComparer.Default.Equals(symbol, local)))
        {
            return true;
        }

        return false;
    }

    private static void AnalyzeVariable(SyntaxNodeAnalysisContext context)
    {
        var declarator = (VariableDeclaratorSyntax)context.Node;
        if (declarator.Initializer is null ||
            declarator.Parent?.Parent is LocalDeclarationStatementSyntax
            { UsingKeyword.RawKind: not 0 } ||
            context.SemanticModel.GetDeclaredSymbol(
                declarator,
                context.CancellationToken) is not ILocalSymbol local ||
            FindExecutableScope(declarator) is not SyntaxNode scope ||
            scope.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Any(identifier => SymbolEqualityComparer.Default.Equals(
                    context.SemanticModel.GetSymbolInfo(
                        identifier,
                        context.CancellationToken).Symbol,
                    local)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            declarator.GetLocation(),
            local.Name));
    }

    private static SyntaxNode? FindExecutableScope(SyntaxNode node)
    {
        for (SyntaxNode? current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or
                LocalFunctionStatementSyntax or
                AccessorDeclarationSyntax or
                BaseMethodDeclarationSyntax)
            {
                return current;
            }
        }

        return null;
    }
}

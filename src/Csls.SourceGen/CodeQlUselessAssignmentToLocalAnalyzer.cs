using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents final constant writes to locals that are never observed.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlUselessAssignmentToLocalAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a side-effect-free local assignment whose value does not flow to a read.
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
        foreach (ISymbol symbol in flow.DataFlowsOut)
        {
            if (SymbolEqualityComparer.Default.Equals(symbol, local))
            {
                return true;
            }
        }

        return false;
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Keeps local writes observable and captured-exception control flow explicit.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlUselessAssignmentToLocalAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a local assignment whose value does not flow to a read.
    /// </summary>
    public const string DiagnosticId = "CSLS0018";

    /// <summary>
    /// Identifies captured-exception rethrows that require an explicit null guard.
    /// </summary>
    public const string ConditionalRethrowDiagnosticId = "CSLS0029";

    private static readonly DiagnosticDescriptor s_conditionalRethrowRule = new(
        ConditionalRethrowDiagnosticId,
        "Use an explicit captured-exception guard",
        "Guard the captured exception explicitly before calling Throw",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Explicit guards preserve the returning path through exception-check helpers in CodeQL control-flow analysis.");

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Remove useless local assignment",
        "Assigned value for local '{0}' is never observed",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Dead local writes must not introduce CodeQL cs/useless-assignment-to-local findings.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_rule, s_conditionalRethrowRule];

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
            AnalyzeConditionalRethrow,
            SyntaxKind.ConditionalAccessExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeAssignment,
            SyntaxKind.SimpleAssignmentExpression);
        context.RegisterSyntaxNodeAction(
            AnalyzeVariable,
            SyntaxKind.VariableDeclarator);
        context.RegisterSyntaxNodeAction(
            AnalyzeForEach,
            SyntaxKind.ForEachStatement);
        context.RegisterSyntaxNodeAction(
            AnalyzeUpdate,
            SyntaxKind.PostIncrementExpression,
            SyntaxKind.PreIncrementExpression,
            SyntaxKind.PostDecrementExpression,
            SyntaxKind.PreDecrementExpression);
    }

    private static void AnalyzeConditionalRethrow(SyntaxNodeAnalysisContext context)
    {
        var access = (ConditionalAccessExpressionSyntax)context.Node;
        if (access.WhenNotNull is InvocationExpressionSyntax invocation &&
            context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is IMethodSymbol
            { Name: "Throw", IsStatic: false, Parameters.Length: 0 } method &&
            method.ContainingType.ToDisplayString() == "System.Runtime.ExceptionServices.ExceptionDispatchInfo")
        {
            context.ReportDiagnostic(Diagnostic.Create(s_conditionalRethrowRule, access.GetLocation()));
        }
    }

    private static void AnalyzeUpdate(SyntaxNodeAnalysisContext context)
    {
        if (context.Node.Parent is not ExpressionStatementSyntax statement ||
            context.SemanticModel.GetOperation(context.Node, context.CancellationToken) is not
            IIncrementOrDecrementOperation { OperatorMethod: null, Target: ILocalReferenceOperation target } ||
            target.Local.RefKind != RefKind.None)
        {
            return;
        }

        DataFlowAnalysis? flow = context.SemanticModel.AnalyzeDataFlow(statement);
        if (flow is null || !flow.Succeeded || FlowsOut(flow, target.Local) ||
            flow.Captured.Any(symbol => SymbolEqualityComparer.Default.Equals(symbol, target.Local)) ||
            HasEscapingReference(context, target.Local))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_rule, context.Node.GetLocation(), target.Local.Name));
    }

    private static bool HasEscapingReference(SyntaxNodeAnalysisContext context, ILocalSymbol local)
    {
        SyntaxNode? scope = FindExecutableScope(context.Node);
        return scope is not null && scope.DescendantNodes().OfType<IdentifierNameSyntax>().Any(identifier =>
            HasReferenceParent(identifier) &&
            SymbolEqualityComparer.Default.Equals(
                context.SemanticModel.GetSymbolInfo(identifier, context.CancellationToken).Symbol, local));
    }

    private static bool HasReferenceParent(SyntaxNode expression)
    {
        while (expression.Parent is ParenthesizedExpressionSyntax parentheses)
        {
            expression = parentheses;
        }

        return expression.Parent is RefExpressionSyntax or ArgumentSyntax { RefKindKeyword.RawKind: not 0 } ||
            expression.Parent is PrefixUnaryExpressionSyntax prefix && prefix.IsKind(SyntaxKind.AddressOfExpression);
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
        => flow.DataFlowsOut.Any(symbol =>
            SymbolEqualityComparer.Default.Equals(symbol, local));

    private static void AnalyzeForEach(SyntaxNodeAnalysisContext context)
    {
        var statement = (ForEachStatementSyntax)context.Node;
        if (statement.Identifier.ValueText == "_" ||
            context.SemanticModel.GetDeclaredSymbol(
                statement,
                context.CancellationToken) is not ILocalSymbol local ||
            statement.Statement.DescendantNodesAndSelf()
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
            statement.Identifier.GetLocation(),
            local.Name));
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

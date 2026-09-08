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
/// Detects dictionary presence guards followed by a redundant lookup of the same key.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlInefficientContainsKeyAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies dictionary reads that repeat a presence lookup.
    /// </summary>
    public const string DiagnosticId = "CSLS0031";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Retrieve dictionary values in the presence lookup",
        "Retrieve the dictionary value with TryGetValue or Remove instead of ContainsKey followed by an indexer",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Dictionary guards must not introduce CodeQL cs/inefficient-containskey findings.");

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
        var syntax = (InvocationExpressionSyntax)context.Node;
        if (syntax.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ContainsKey" } ||
            context.SemanticModel.GetOperation(syntax, context.CancellationToken) is not IInvocationOperation call ||
            call.Arguments.Length != 1 || !IsDictionaryMember(call.TargetMethod) ||
            GetGuardedRead(syntax) is not { } candidate)
        {
            return;
        }

        bool repeatsLookup = candidate.DescendantNodesAndSelf(
            static node => node is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
            .OfType<ElementAccessExpressionSyntax>().Any(access =>
                context.SemanticModel.GetOperation(access, context.CancellationToken) is IPropertyReferenceOperation
                { Property.IsIndexer: true, Arguments.Length: 1 } indexer &&
                IsDictionaryMember(indexer.Property) &&
                IsSameValue(call.Instance, indexer.Instance) &&
                IsSameValue(call.Arguments[0].Value, indexer.Arguments[0].Value) &&
                IsUnchangedRead(candidate, access));
        if (repeatsLookup)
        {
            context.ReportDiagnostic(Diagnostic.Create(s_rule, syntax.GetLocation()));
        }
    }

    private static bool IsDictionaryMember(ISymbol member)
    {
        INamedTypeSymbol owner = member.ContainingType;
        return owner.AllInterfaces.Add(owner).Where(static type =>
            type.OriginalDefinition.MetadataName is "IDictionary`2" or "IReadOnlyDictionary`2" &&
            type.ContainingNamespace.ToDisplayString() == "System.Collections.Generic")
            .SelectMany(type => type.GetMembers(member.Name)).Any(contract =>
                SymbolEqualityComparer.Default.Equals(contract, member) ||
                SymbolEqualityComparer.Default.Equals(owner.FindImplementationForInterfaceMember(contract), member));
    }

    private static SyntaxNode? GetGuardedRead(InvocationExpressionSyntax invocation)
    {
        ExpressionSyntax condition = SkipParentheses(invocation);
        bool negated = condition.Parent is PrefixUnaryExpressionSyntax unary &&
            unary.IsKind(SyntaxKind.LogicalNotExpression);
        if (negated && condition.Parent is PrefixUnaryExpressionSyntax negative)
        {
            condition = SkipParentheses(negative);
        }

        return condition.Parent switch
        {
            IfStatementSyntax branch when branch.Condition == condition => negated
                ? branch.Else is { } alternative ? FirstStatement(alternative.Statement)
                    : Exits(branch.Statement) ? NextStatement(branch) : null
                : FirstStatement(branch.Statement),
            WhileStatementSyntax loop when loop.Condition == condition => negated
                ? loop.Statement.DescendantNodesAndSelf().OfType<BreakStatementSyntax>().Any() ? null : NextStatement(loop)
                : FirstStatement(loop.Statement),
            ConditionalExpressionSyntax choice when choice.Condition == condition =>
                negated ? choice.WhenFalse : choice.WhenTrue,
            _ => null
        };
    }

    private static ExpressionSyntax SkipParentheses(ExpressionSyntax expression)
    {
        while (expression.Parent is ParenthesizedExpressionSyntax parent)
        {
            expression = parent;
        }
        return expression;
    }

    private static StatementSyntax? FirstStatement(StatementSyntax statement) =>
        statement is BlockSyntax block ? block.Statements.FirstOrDefault() : statement;

    private static bool Exits(StatementSyntax statement) =>
        FirstStatement(statement) is ReturnStatementSyntax or ThrowStatementSyntax;

    private static StatementSyntax? NextStatement(StatementSyntax statement)
    {
        if (statement.Parent is not BlockSyntax block)
        {
            return null;
        }
        int next = block.Statements.IndexOf(statement) + 1;
        return next < block.Statements.Count ? block.Statements[next] : null;
    }

    private static bool IsUnchangedRead(SyntaxNode candidate, ElementAccessExpressionSyntax access)
    {
        ExpressionSyntax target = SkipParentheses(access);
        if (target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target &&
            assignment.IsKind(SyntaxKind.SimpleAssignmentExpression))
        {
            return false;
        }

        return !candidate.DescendantNodesAndSelf().Where(node => node.SpanStart < access.SpanStart).Any(static node =>
            node is AssignmentExpressionSyntax or InvocationExpressionSyntax or AwaitExpressionSyntax ||
            node is ArgumentSyntax argument && !argument.RefKindKeyword.IsKind(SyntaxKind.None) ||
            node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PreDecrementExpression) ||
            node.IsKind(SyntaxKind.PostIncrementExpression) || node.IsKind(SyntaxKind.PostDecrementExpression));
    }

    private static bool IsSameValue(IOperation? left, IOperation? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }
        if (left is IConversionOperation { IsImplicit: true } leftConversion)
        {
            return IsSameValue(leftConversion.Operand, right);
        }
        if (right is IConversionOperation { IsImplicit: true } rightConversion)
        {
            return IsSameValue(left, rightConversion.Operand);
        }
        return (left, right) switch
        {
            (ILocalReferenceOperation first, ILocalReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Local, second.Local),
            (IParameterReferenceOperation first, IParameterReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Parameter, second.Parameter),
            (IFieldReferenceOperation first, IFieldReferenceOperation second) =>
                SymbolEqualityComparer.Default.Equals(first.Field, second.Field) && IsSameValue(first.Instance, second.Instance),
            (IInstanceReferenceOperation first, IInstanceReferenceOperation second) =>
                first.ReferenceKind == second.ReferenceKind && SymbolEqualityComparer.Default.Equals(first.Type, second.Type),
            (ILiteralOperation first, ILiteralOperation second) =>
                Equals(first.ConstantValue, second.ConstantValue) && SymbolEqualityComparer.Default.Equals(first.Type, second.Type),
            _ => false
        };
    }
}

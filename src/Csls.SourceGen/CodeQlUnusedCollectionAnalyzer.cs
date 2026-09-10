using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents mutation-only collection aliases reported by CodeQL's unused-collection query.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlUnusedCollectionAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a deconstructed collection alias whose contents are never read.
    /// </summary>
    public const string DiagnosticId = "CSLS0014";

    private static readonly ImmutableHashSet<string> s_mutatingMethodNames =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Add",
            "AddRange",
            "Clear",
            "Enqueue",
            "ExceptWith",
            "Insert",
            "InsertRange",
            "IntersectWith",
            "Pop",
            "Push",
            "Remove",
            "RemoveAll",
            "RemoveAt",
            "RemoveRange",
            "Reverse",
            "Sort",
            "SymmetricExceptWith",
            "TryAdd",
            "UnionWith");

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Read collection contents through the same local",
        "Collection alias '{0}' is only mutated; avoid the alias or read its contents directly",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Mutation-only collection aliases must not introduce CodeQL cs/unused-collection findings.");

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
            AnalyzeDesignation,
            SyntaxKind.SingleVariableDesignation);
    }

    private static void AnalyzeDesignation(SyntaxNodeAnalysisContext context)
    {
        var designation = (SingleVariableDesignationSyntax)context.Node;
        if (designation.Parent is not DeclarationExpressionSyntax ||
            designation.Ancestors().OfType<AssignmentExpressionSyntax>().FirstOrDefault() is
                not { Left: { } left } assignment ||
            !left.Span.Contains(designation.Span) ||
            context.SemanticModel.GetDeclaredSymbol(
                designation,
                context.CancellationToken) is not ILocalSymbol local ||
            !IsCollection(local.Type))
        {
            return;
        }

        SyntaxNode? scope = assignment.Ancestors().FirstOrDefault(static ancestor =>
            ancestor is AnonymousFunctionExpressionSyntax or
                LocalFunctionStatementSyntax or
                BaseMethodDeclarationSyntax);
        if (scope is null)
        {
            return;
        }

        IdentifierNameSyntax[] references =
        [
            .. scope.DescendantNodes()
                .OfType<IdentifierNameSyntax>()
                .Where(identifier => SymbolEqualityComparer.Default.Equals(
                    local,
                    context.SemanticModel.GetSymbolInfo(
                        identifier,
                        context.CancellationToken).Symbol))
        ];
        if (references.Length == 0 ||
            references.Any(reference => !IsMutationOnlyUse(reference, context)))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            designation.GetLocation(),
            designation.Identifier.ValueText));
    }

    private static bool IsCollection(ITypeSymbol type) =>
        type.AllInterfaces.Any(static candidate =>
            candidate.OriginalDefinition.SpecialType ==
                SpecialType.System_Collections_Generic_ICollection_T);

    private static bool IsMutationOnlyUse(
        IdentifierNameSyntax identifier,
        SyntaxNodeAnalysisContext context)
    {
        if (identifier.Parent is not MemberAccessExpressionSyntax access ||
            access.Expression != identifier ||
            access.Parent is not InvocationExpressionSyntax invocation ||
            context.SemanticModel.GetSymbolInfo(
                invocation,
                context.CancellationToken).Symbol is not IMethodSymbol method)
        {
            return false;
        }

        return s_mutatingMethodNames.Contains(method.Name);
    }
}

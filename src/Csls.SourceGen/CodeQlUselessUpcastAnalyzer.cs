using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents redundant nested upcasts that CodeQL reports as useless conversions.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlUselessUpcastAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies nested explicit casts whose inner conversion is already implicit.
    /// </summary>
    public const string DiagnosticId = "CSLS0016";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Remove redundant nested upcast",
        "Explicit conversion to '{0}' is implicit and redundant inside the outer cast",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Nested upcasts must not introduce CodeQL cs/useless-upcast findings.");

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
        context.RegisterSyntaxNodeAction(AnalyzeCast, SyntaxKind.CastExpression);
    }

    private static void AnalyzeCast(SyntaxNodeAnalysisContext context)
    {
        var cast = (CastExpressionSyntax)context.Node;
        if (context.SemanticModel.GetTypeInfo(cast.Type, context.CancellationToken).Type
                is not ITypeSymbol targetType)
        {
            return;
        }

        if (IsRedundantNullUpcast(cast, targetType, context))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_rule,
                cast.GetLocation(),
                targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            return;
        }

        if (cast.Parent is not CastExpressionSyntax ||
            context.SemanticModel.GetTypeInfo(cast.Expression, context.CancellationToken).Type
                is not ITypeSymbol sourceType)
        {
            return;
        }

        Conversion conversion = context.Compilation.ClassifyConversion(
            sourceType,
            targetType);
        if (!conversion.IsImplicit || conversion.IsIdentity)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            cast.GetLocation(),
            targetType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    private static bool IsRedundantNullUpcast(
        CastExpressionSyntax cast,
        ITypeSymbol targetType,
        SyntaxNodeAnalysisContext context)
    {
        if (!cast.Expression.IsKind(SyntaxKind.NullLiteralExpression) ||
            cast.Parent is not EqualsValueClauseSyntax equalsValue ||
            equalsValue.Parent is not VariableDeclaratorSyntax declarator ||
            context.SemanticModel.GetDeclaredSymbol(
                declarator,
                context.CancellationToken) is not ILocalSymbol local)
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(local.Type, targetType);
    }
}

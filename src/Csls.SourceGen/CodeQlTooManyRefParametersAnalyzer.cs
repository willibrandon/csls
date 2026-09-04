using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents methods with excessive by-reference parameter state reported by CodeQL.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlTooManyRefParametersAnalyzer : DiagnosticAnalyzer
{
    private const int MaximumByReferenceParameterCount = 2;

    /// <summary>
    /// Identifies a method with too many by-reference parameters.
    /// </summary>
    public const string DiagnosticId = "CSLS0012";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Encapsulate by-reference method state",
        "Method '{0}' has {1} by-reference parameters; encapsulate shared state instead",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Methods must not introduce CodeQL cs/too-many-ref-parameters findings.");

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
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        int byReferenceCount = method.Parameters.Count(static parameter =>
            parameter.RefKind is RefKind.Ref or RefKind.Out);
        if (byReferenceCount <= MaximumByReferenceParameterCount ||
            method.ContainingType.TypeKind == TypeKind.Interface ||
            method.IsOverride ||
            ImplementsInterfaceContract(method) ||
            method.Locations.FirstOrDefault(static location => location.IsInSource) is not
                Location location)
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            s_rule,
            location,
            method.Name,
            byReferenceCount));
    }

    private static bool ImplementsInterfaceContract(IMethodSymbol method)
    {
        foreach (INamedTypeSymbol @interface in method.ContainingType.AllInterfaces)
        {
            foreach (ISymbol member in @interface.GetMembers(method.Name))
            {
                if (SymbolEqualityComparer.Default.Equals(
                    method.ContainingType.FindImplementationForInterfaceMember(member),
                    method))
                {
                    return true;
                }
            }
        }

        return false;
    }
}

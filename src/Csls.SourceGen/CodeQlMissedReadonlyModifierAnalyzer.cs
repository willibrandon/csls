using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Prevents mutable source fields that CodeQL can prove are initialization-only.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlMissedReadonlyModifierAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies a field that must declare the readonly modifier.
    /// </summary>
    public const string DiagnosticId = "CSLS0011";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Mark initialization-only fields readonly",
        "Field '{0}' is assigned only during initialization and must be readonly",
        "CodeQuality",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Initialization-only fields must not introduce CodeQL cs/missed-readonly-modifier findings.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

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
        context.RegisterCompilationStartAction(static startContext =>
        {
            var candidates = new ConcurrentDictionary<IFieldSymbol, Location>(
                SymbolEqualityComparer.Default);
            var disqualifyingWrites = new ConcurrentDictionary<IFieldSymbol, byte>(
                SymbolEqualityComparer.Default);
            startContext.RegisterSymbolAction(
                symbolContext => CollectCandidate(symbolContext, candidates),
                SymbolKind.Field);
            startContext.RegisterOperationAction(
                operationContext => CollectWrite(operationContext, disqualifyingWrites),
                OperationKind.FieldReference);
            startContext.RegisterCompilationEndAction(endContext =>
            {
                foreach (KeyValuePair<IFieldSymbol, Location> candidate in candidates.Where(
                    candidate => !disqualifyingWrites.ContainsKey(candidate.Key)))
                {
                    endContext.ReportDiagnostic(Diagnostic.Create(
                        s_rule,
                        candidate.Value,
                        candidate.Key.Name));
                }
            });
        });
    }

    private static void CollectCandidate(
        SymbolAnalysisContext context,
        ConcurrentDictionary<IFieldSymbol, Location> candidates)
    {
        var field = (IFieldSymbol)context.Symbol;
        if (field.IsImplicitlyDeclared ||
            field.IsConst ||
            field.IsReadOnly ||
            field.IsVolatile ||
            field.ContainingType.TypeKind != TypeKind.Class ||
            field.DeclaredAccessibility is not (
                Accessibility.Private or Accessibility.Internal) ||
            field.Locations.FirstOrDefault(static location => location.IsInSource) is not
                Location location)
        {
            return;
        }

        candidates.TryAdd(field, location);
    }

    private static void CollectWrite(
        OperationAnalysisContext context,
        ConcurrentDictionary<IFieldSymbol, byte> disqualifyingWrites)
    {
        var reference = (IFieldReferenceOperation)context.Operation;
        if (!IsWrite(reference) || IsInitializationWrite(reference.Field, context.ContainingSymbol))
        {
            return;
        }

        disqualifyingWrites.TryAdd(reference.Field, 0);
    }

    private static bool IsWrite(IFieldReferenceOperation reference)
    {
        IOperation? current = reference;
        while (current.Parent is IConversionOperation or IParenthesizedOperation)
        {
            current = current.Parent;
        }

        return current.Parent switch
        {
            ISimpleAssignmentOperation assignment =>
                ReferenceEquals(assignment.Target, current),
            ICompoundAssignmentOperation assignment =>
                ReferenceEquals(assignment.Target, current),
            ICoalesceAssignmentOperation assignment =>
                ReferenceEquals(assignment.Target, current),
            IIncrementOrDecrementOperation increment =>
                ReferenceEquals(increment.Target, current),
            IArgumentOperation argument => argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out,
            IAddressOfOperation => true,
            _ => false
        };
    }

    private static bool IsInitializationWrite(IFieldSymbol field, ISymbol containingSymbol)
    {
        if (SymbolEqualityComparer.Default.Equals(field, containingSymbol))
        {
            return true;
        }

        return containingSymbol is IMethodSymbol method &&
            SymbolEqualityComparer.Default.Equals(field.ContainingType, method.ContainingType) &&
            (field.IsStatic
                ? method.MethodKind == MethodKind.StaticConstructor
                : method.MethodKind == MethodKind.Constructor);
    }
}

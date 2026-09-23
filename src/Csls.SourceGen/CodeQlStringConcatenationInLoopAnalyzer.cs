using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Immutable;
using System.Linq;

namespace Csls.SourceGen;

/// <summary>
/// Detects string accumulation into variables that survive successive loop iterations.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlStringConcatenationInLoopAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies repeated string concatenation that should use a mutable string builder.
    /// </summary>
    public const string DiagnosticId = "CSLS0027";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId, "Use a string builder for loop accumulation",
        "Use StringBuilder instead of accumulating string '{0}' with concatenation in a loop",
        "CodeQuality", DiagnosticSeverity.Error, isEnabledByDefault: true,
        description: "String accumulation must not introduce CodeQL cs/string-concatenation-in-loop findings.");

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
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.CompoundAssignment, OperationKind.SimpleAssignment);
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var assignment = (IAssignmentOperation)context.Operation;
        if (assignment.Target.Type?.SpecialType != SpecialType.System_String ||
            GetVariable(assignment.Target) is not ISymbol variable ||
            !IsConcatenation(assignment, variable) ||
            !SurvivesLoop(assignment, variable))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_rule, assignment.Syntax.GetLocation(), variable.Name));
    }

    private static bool IsConcatenation(IAssignmentOperation assignment, ISymbol variable) =>
        assignment is ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add, OperatorMethod: null } ||
        assignment is ISimpleAssignmentOperation &&
        assignment.Value is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } addition &&
        (ContainsVariable(addition.LeftOperand, variable) || ContainsVariable(addition.RightOperand, variable));

    private static bool ContainsVariable(IOperation operand, ISymbol variable)
    {
        while (operand is IConversionOperation { OperatorMethod: null } conversion)
        {
            operand = conversion.Operand;
        }

        return SymbolEqualityComparer.Default.Equals(GetVariable(operand), variable) ||
            operand is IBinaryOperation { OperatorKind: BinaryOperatorKind.Add, Type.SpecialType: SpecialType.System_String } addition &&
            (ContainsVariable(addition.LeftOperand, variable) || ContainsVariable(addition.RightOperand, variable));
    }

    private static ISymbol? GetVariable(IOperation operation) => operation switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        _ => null
    };

    private static bool SurvivesLoop(IOperation assignment, ISymbol variable)
    {
        for (IOperation? ancestor = assignment.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return false;
            }

            if (ancestor is ILoopOperation loop &&
                (variable is not ILocalSymbol || !variable.DeclaringSyntaxReferences.Any(reference =>
                    reference.SyntaxTree == loop.Syntax.SyntaxTree && loop.Syntax.Span.Contains(reference.Span))))
            {
                return true;
            }
        }

        return false;
    }
}

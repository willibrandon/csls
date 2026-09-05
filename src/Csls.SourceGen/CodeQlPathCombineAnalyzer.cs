using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Immutable;

namespace Csls.SourceGen;

/// <summary>
/// Prevents path composition from silently discarding preceding components.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CodeQlPathCombineAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Identifies calls to the path-combination API that can replace earlier components.
    /// </summary>
    public const string DiagnosticId = "CSLS0022";

    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticId,
        "Preserve preceding path components",
        "Use Path.Join instead of Path.Combine to preserve preceding path components",
        "Reliability",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Calls to System.IO.Path.Combine must not introduce CodeQL cs/path-combine findings.");

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
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (method.Name != "Combine" ||
            method.ContainingType.ToDisplayString() != "System.IO.Path")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(s_rule, invocation.Syntax.GetLocation()));
    }
}

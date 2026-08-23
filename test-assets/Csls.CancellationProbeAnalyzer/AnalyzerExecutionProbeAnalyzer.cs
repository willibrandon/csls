using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Csls.Testing;

/// <summary>
/// Reports real source diagnostics after an observable project-wide analyzer execution.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AnalyzerExecutionProbeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_descriptor = new(
        "CSLSTEST002",
        "Analyzer execution probe",
        "Analyzer execution probe",
        "Testing",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    /// <summary>
    /// Gets the diagnostic contract reported once for every source document.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_descriptor];

    /// <summary>
    /// Registers one real project-wide analyzer execution probe.
    /// </summary>
    /// <param name="context">The Roslyn analyzer initialization context.</param>
    public override void Initialize(AnalysisContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationAction(AnalyzeCompilation);
    }

    private static void AnalyzeCompilation(CompilationAnalysisContext context)
    {
        AdditionalText? marker = context.Options.AdditionalFiles.SingleOrDefault(static file =>
            string.Equals(
                Path.GetFileName(file.Path),
                "AnalyzerExecutionProbe.marker",
                StringComparison.Ordinal));
        if (marker is null)
        {
            return;
        }

        AnalyzerExecutionProbeTransport.WaitForRelease(
            marker.Path,
            context.CancellationToken);
        foreach (SyntaxTree syntaxTree in context.Compilation.SyntaxTrees)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                s_descriptor,
                Location.Create(syntaxTree, new TextSpan(0, 0))));
        }
    }
}

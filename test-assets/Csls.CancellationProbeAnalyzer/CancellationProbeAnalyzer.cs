using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;

namespace Csls.Testing;

/// <summary>
/// Waits inside real Roslyn analyzer execution until its request token is canceled.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CancellationProbeAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_descriptor = new(
        "CSLSTEST001",
        "Cancellation probe",
        "Cancellation probe",
        "Testing",
        DiagnosticSeverity.Hidden,
        isEnabledByDefault: true);

    /// <summary>
    /// Gets the diagnostic contract that makes the cancellation probe executable by Roslyn.
    /// </summary>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [s_descriptor];

    /// <summary>
    /// Registers one real compilation action that observes analyzer cancellation.
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
        AdditionalText marker = context.Options.AdditionalFiles.Single(static file =>
            string.Equals(
                Path.GetFileName(file.Path),
                "CancellationProbe.marker",
                StringComparison.Ordinal));
        CancellationProbeTransport.WaitForCancellation(
            marker.Path,
            context.CancellationToken);
    }
}

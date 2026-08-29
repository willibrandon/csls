using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using System.Collections.Concurrent;
using System.Runtime.Loader;

namespace Csls.Workspaces;

/// <summary>
/// Discovers Roslyn's real host analyzers from its composed feature assemblies.
/// </summary>
internal sealed class RoslynDiagnosticAnalyzerCatalog
{
    private readonly IReadOnlyList<AnalyzerFileReference> _references;
    private readonly ConcurrentDictionary<string, IReadOnlyList<DiagnosticAnalyzer>>
        _analyzersByLanguage = new(StringComparer.Ordinal);

    private RoslynDiagnosticAnalyzerCatalog(
        IReadOnlyList<AnalyzerFileReference> references)
    {
        _references = references;
    }

    /// <summary>
    /// Creates the host analyzer catalog for the installed Roslyn composition.
    /// </summary>
    internal static RoslynDiagnosticAnalyzerCatalog Create()
    {
        string[] assemblyPaths =
        [
            .. MefHostServices.DefaultAssemblies
                .Select(static assembly => assembly.Location)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
        ];
        var loader = new RoslynAnalyzerAssemblyLoader(
            AssemblyLoadContext.GetLoadContext(typeof(DiagnosticAnalyzer).Assembly)
                ?? throw new InvalidOperationException(
                    "Roslyn's analyzer assembly load context is unavailable."));
        foreach (string path in assemblyPaths)
        {
            loader.AddDependencyLocation(path);
        }

        return new RoslynDiagnosticAnalyzerCatalog(
        [
            .. assemblyPaths.Select(path => new AnalyzerFileReference(path, loader))
        ]);
    }

    /// <summary>
    /// Gets the host diagnostic analyzers exported for a source language.
    /// </summary>
    internal IReadOnlyList<DiagnosticAnalyzer> GetAnalyzers(string language) =>
        _analyzersByLanguage.GetOrAdd(language, GetAnalyzersCore);

    private IReadOnlyList<DiagnosticAnalyzer> GetAnalyzersCore(string language) =>
    [
        .. _references
            .SelectMany(reference => reference.GetAnalyzers(language))
            .DistinctBy(static analyzer => analyzer.GetType().FullName)
            .OrderBy(static analyzer => analyzer.GetType().FullName, StringComparer.Ordinal)
    ];
}

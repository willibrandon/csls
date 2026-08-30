using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Host.Mef;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Csls.Workspaces;

/// <summary>
/// Discovers Roslyn's real host analyzers from its composed feature assemblies.
/// </summary>
internal sealed class RoslynDiagnosticAnalyzerCatalog
{
    private readonly IReadOnlyList<AnalyzerFileReference> _references;
    private readonly IReadOnlyList<Assembly> _locationlessAssemblies;
    private readonly ConcurrentDictionary<string, IReadOnlyList<DiagnosticAnalyzer>>
        _analyzersByLanguage = new(StringComparer.Ordinal);

    private RoslynDiagnosticAnalyzerCatalog(
        IReadOnlyList<AnalyzerFileReference> references,
        IReadOnlyList<Assembly> locationlessAssemblies)
    {
        _references = references;
        _locationlessAssemblies = locationlessAssemblies;
    }

    /// <summary>
    /// Creates the host analyzer catalog for the installed Roslyn composition.
    /// </summary>
    internal static RoslynDiagnosticAnalyzerCatalog Create()
    {
        Assembly[] assemblies =
        [
            .. MefHostServices.DefaultAssemblies
                .DistinctBy(static assembly => assembly.FullName)
        ];
        string[] assemblyPaths =
        [
            .. assemblies
                .Select(static assembly => assembly.Location)
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.Ordinal)
        ];
        Assembly[] locationlessAssemblies =
        [
            .. assemblies.Where(static assembly =>
                string.IsNullOrWhiteSpace(assembly.Location))
        ];
        if (assemblyPaths.Length == 0)
        {
            return new RoslynDiagnosticAnalyzerCatalog([], locationlessAssemblies);
        }

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
        ],
        locationlessAssemblies);
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
            .Concat(_locationlessAssemblies.SelectMany(assembly =>
                GetAnalyzers(assembly, language)))
            .DistinctBy(static analyzer => analyzer.GetType().FullName)
            .OrderBy(static analyzer => analyzer.GetType().FullName, StringComparer.Ordinal)
    ];

    private static IEnumerable<DiagnosticAnalyzer> GetAnalyzers(
        Assembly assembly,
        string language)
    {
        foreach (Type type in assembly.GetTypes())
        {
            if (type.IsAbstract || !typeof(DiagnosticAnalyzer).IsAssignableFrom(type))
            {
                continue;
            }

            DiagnosticAnalyzerAttribute? attribute = type
                .GetCustomAttribute<DiagnosticAnalyzerAttribute>(inherit: false);
            if (attribute is null ||
                !attribute.Languages.Contains(language, StringComparer.Ordinal))
            {
                continue;
            }

            if (Activator.CreateInstance(type, nonPublic: true) is DiagnosticAnalyzer analyzer)
            {
                yield return analyzer;
            }
        }
    }
}

using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace Csls.Workspaces;

/// <summary>
/// Loads Roslyn host analyzer assemblies and their registered dependencies.
/// </summary>
internal sealed class RoslynAnalyzerAssemblyLoader : IAnalyzerAssemblyLoader
{
    private readonly AssemblyLoadContext _loadContext;
    private readonly ConcurrentDictionary<string, string> _dependencyPaths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a loader in the compiler's existing assembly load context.
    /// </summary>
    internal RoslynAnalyzerAssemblyLoader(AssemblyLoadContext loadContext)
    {
        _loadContext = loadContext;
        _loadContext.Resolving += Resolve;
    }

    /// <summary>
    /// Registers an analyzer dependency location before any analyzer is loaded.
    /// </summary>
    public void AddDependencyLocation(string fullPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        string simpleName = AssemblyName.GetAssemblyName(fullPath).Name
            ?? throw new InvalidDataException(
                $"Analyzer dependency {fullPath} has no assembly name.");
        _dependencyPaths[simpleName] = fullPath;
    }

    /// <summary>
    /// Loads an analyzer assembly from its registered absolute path.
    /// </summary>
    public Assembly LoadFromPath(string fullPath)
    {
        AddDependencyLocation(fullPath);
        return _loadContext.LoadFromAssemblyPath(fullPath);
    }

    private Assembly? Resolve(
        AssemblyLoadContext loadContext,
        AssemblyName assemblyName)
    {
        if (assemblyName.Name is null ||
            !_dependencyPaths.TryGetValue(assemblyName.Name, out string? path))
        {
            return null;
        }

        return loadContext.LoadFromAssemblyPath(path);
    }
}

using Microsoft.CodeAnalysis;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace Csls.Workspaces;

/// <summary>
/// Keeps Roslyn's source indexes in memory when persistent storage is unavailable in a browser.
/// </summary>
internal static class BrowserSyntaxIndexCache
{
    private static readonly string[] s_indexTypeNames =
    [
        "Microsoft.CodeAnalysis.FindSymbols.SyntaxTreeIndex",
        "Microsoft.CodeAnalysis.FindSymbols.TopLevelSyntaxTreeIndex"
    ];

    /// <summary>
    /// Creates missing source indexes for every current document in a browser solution.
    /// </summary>
    /// <param name="solution">The immutable solution whose documents will be indexed.</param>
    /// <param name="cancellationToken">The indexing cancellation token.</param>
    /// <returns>A task that completes when all current document states are indexed.</returns>
    internal static async Task WarmAsync(
        Solution solution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solution);
        if (!OperatingSystem.IsBrowser())
        {
            return;
        }

        Assembly workspaceAssembly = typeof(Workspace).Assembly;
        PropertyInfo projectStateProperty = GetRequiredProperty(typeof(Project), "State");
        PropertyInfo documentStateProperty = GetRequiredProperty(typeof(TextDocument), "State");
        foreach (Project project in solution.Projects)
        {
            object projectState = projectStateProperty.GetValue(project)
                ?? throw new InvalidOperationException("Roslyn's project state is unavailable.");
            foreach (Document document in project.Documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                object documentState = documentStateProperty.GetValue(document)
                    ?? throw new InvalidOperationException("Roslyn's document state is unavailable.");
                SyntaxNode? root = null;
                foreach (string indexTypeName in s_indexTypeNames)
                {
                    Type indexType = workspaceAssembly.GetType(indexTypeName, throwOnError: true)
                        ?? throw new InvalidOperationException(
                            $"Roslyn index type {indexTypeName} is unavailable.");
                    Type baseType = indexType.BaseType
                        ?? throw new InvalidOperationException(
                            $"Roslyn index type {indexTypeName} has no base type.");
                    FieldInfo cacheField = baseType.GetField(
                        "s_documentToIndex",
                        BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new InvalidOperationException(
                            $"Roslyn index cache {indexTypeName} is unavailable.");
                    object cache = cacheField.GetValue(null)
                        ?? throw new InvalidOperationException(
                            $"Roslyn index cache {indexTypeName} was not initialized.");
                    MethodInfo tryGetValueMethod = cache.GetType().GetMethod(
                        "TryGetValue",
                        BindingFlags.Public | BindingFlags.Instance)
                        ?? throw new InvalidOperationException(
                            $"Roslyn index cache {indexTypeName} cannot be queried.");
                    if (Contains(cache, tryGetValueMethod, documentState))
                    {
                        continue;
                    }

                    root ??= await document.GetSyntaxRootAsync(cancellationToken)
                        .ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            $"Roslyn returned no syntax root for {document.FilePath}.");
                    MethodInfo createIndexMethod = indexType.GetMethod(
                        "CreateIndex",
                        BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new InvalidOperationException(
                            $"Roslyn index factory {indexTypeName} is unavailable.");
                    object index = Invoke(
                        createIndexMethod,
                        target: null,
                        [projectState, root, null, cancellationToken])
                        ?? throw new InvalidOperationException(
                            $"Roslyn index factory {indexTypeName} returned no index.");
                    MethodInfo tryAddMethod = cache.GetType().GetMethod(
                        "TryAdd",
                        BindingFlags.Public | BindingFlags.Instance)
                        ?? throw new InvalidOperationException(
                            $"Roslyn index cache {indexTypeName} cannot be populated.");
                    Invoke(tryAddMethod, cache, [documentState, index]);
                }
            }
        }
    }

    private static bool Contains(
        object cache,
        MethodInfo tryGetValueMethod,
        object documentState)
    {
        object?[] arguments = [documentState, null];
        return Invoke(tryGetValueMethod, cache, arguments) is true;
    }

    private static PropertyInfo GetRequiredProperty(Type type, string propertyName) =>
        type.GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException(
            $"Roslyn property {type.FullName}.{propertyName} is unavailable.");

    private static object? Invoke(
        MethodInfo method,
        object? target,
        object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }
}

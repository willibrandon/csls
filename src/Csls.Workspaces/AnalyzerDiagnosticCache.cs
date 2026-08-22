using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Csls.Workspaces;

/// <summary>
/// Shares one Roslyn diagnostic computation per project and immutable workspace generation.
/// </summary>
internal sealed class AnalyzerDiagnosticCache
{
    private readonly ConcurrentDictionary<
        (long Generation, ProjectId ProjectId),
        Task<ImmutableArray<RoslynDiagnostic>>> _entries = new();

    /// <summary>
    /// Gets or computes all compiler and analyzer diagnostics for one project snapshot.
    /// </summary>
    /// <param name="generation">The immutable workspace generation.</param>
    /// <param name="project">The Roslyn project snapshot.</param>
    /// <param name="factory">The cancellable diagnostic computation.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>All project diagnostics for the requested snapshot.</returns>
    internal async Task<ImmutableArray<RoslynDiagnostic>> GetOrAddAsync(
        long generation,
        Project project,
        Func<Project, CancellationToken, Task<ImmutableArray<RoslynDiagnostic>>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(factory);
        (long Generation, ProjectId ProjectId) key = (generation, project.Id);
        Task<ImmutableArray<RoslynDiagnostic>> task = _entries.GetOrAdd(
            key,
            _ => factory(project, cancellationToken));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _entries.TryRemove(key, out _);
            throw;
        }
    }

    /// <summary>
    /// Removes results associated with superseded workspace snapshots.
    /// </summary>
    internal void Clear() => _entries.Clear();
}

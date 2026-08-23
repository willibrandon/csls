using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Csls.Workspaces;

/// <summary>
/// Shares one Roslyn diagnostic computation per project and immutable workspace generation.
/// </summary>
internal sealed class AnalyzerDiagnosticCache
{
    private readonly ConcurrentDictionary<
        (long Generation, ProjectId ProjectId),
        AnalyzerDiagnosticCacheEntry> _entries = new();

    /// <summary>
    /// Gets the number of project diagnostic computations retained by the cache.
    /// </summary>
    internal int Count => _entries.Count;

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
        cancellationToken.ThrowIfCancellationRequested();
        (long Generation, ProjectId ProjectId) key = (generation, project.Id);
        AnalyzerDiagnosticCacheEntry entry;
        Task<ImmutableArray<RoslynDiagnostic>> computation;
        while (true)
        {
            entry = _entries.GetOrAdd(
                key,
                static (_, state) => new AnalyzerDiagnosticCacheEntry(
                    state.Project,
                    state.Factory),
                (Project: project, Factory: factory));
            if (entry.TryAcquire(out computation))
            {
                break;
            }

            _entries.TryRemove(new KeyValuePair<
                (long Generation, ProjectId ProjectId),
                AnalyzerDiagnosticCacheEntry>(key, entry));
        }

        try
        {
            return await computation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (entry.Release())
            {
                _entries.TryRemove(new KeyValuePair<
                    (long Generation, ProjectId ProjectId),
                    AnalyzerDiagnosticCacheEntry>(key, entry));
            }
        }
    }

    /// <summary>
    /// Removes results associated with superseded workspace snapshots.
    /// </summary>
    internal void Clear()
    {
        foreach (KeyValuePair<
            (long Generation, ProjectId ProjectId),
            AnalyzerDiagnosticCacheEntry> entry in _entries)
        {
            entry.Value.Dispose();
            _entries.TryRemove(entry);
        }
    }
}

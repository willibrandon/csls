using Microsoft.CodeAnalysis;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Csls.Workspaces;

/// <summary>
/// Shares one Roslyn diagnostic computation per immutable project version.
/// </summary>
internal sealed class AnalyzerDiagnosticCache
{
    private readonly ConcurrentDictionary<
        (ProjectId ProjectId, VersionStamp Version),
        AnalyzerDiagnosticCacheEntry> _entries = new();
    private readonly Lock _versionGate = new();
    private readonly Dictionary<
        ProjectId,
        (long Generation, VersionStamp Version)> _versions = [];

    /// <summary>
    /// Gets the number of project diagnostic computations retained by the cache.
    /// </summary>
    internal int Count => _entries.Count;

    /// <summary>
    /// Gets or computes all compiler and analyzer diagnostics for one project snapshot.
    /// </summary>
    /// <param name="generation">The workspace generation that selected the project.</param>
    /// <param name="version">The project version including transitive dependencies.</param>
    /// <param name="project">The Roslyn project snapshot.</param>
    /// <param name="factory">The cancellable diagnostic computation.</param>
    /// <param name="cancellationToken">The request cancellation token.</param>
    /// <returns>All project diagnostics for the requested snapshot.</returns>
    internal async Task<ImmutableArray<RoslynDiagnostic>> GetOrAddAsync(
        long generation,
        VersionStamp version,
        Project project,
        Func<Project, CancellationToken, Task<ImmutableArray<RoslynDiagnostic>>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        (ProjectId ProjectId, VersionStamp Version) key = (project.Id, version);
        if (!TrySelectVersion(generation, key))
        {
            return await factory(project, cancellationToken).ConfigureAwait(false);
        }

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
            try
            {
                if (!IsSelectedVersion(key))
                {
                    TryRemoveAndDispose(key, entry);
                    return await factory(project, cancellationToken).ConfigureAwait(false);
                }

                if (entry.TryAcquire(out computation))
                {
                    break;
                }

                _entries.TryRemove(new KeyValuePair<
                    (ProjectId ProjectId, VersionStamp Version),
                    AnalyzerDiagnosticCacheEntry>(key, entry));
            }
            catch
            {
                TryRemoveAndDispose(key, entry);
                throw;
            }
        }

        int released = 0;
        void ReleaseEntry()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return;
            }

            if (entry.Release())
            {
                _entries.TryRemove(new KeyValuePair<
                    (ProjectId ProjectId, VersionStamp Version),
                    AnalyzerDiagnosticCacheEntry>(key, entry));
            }
        }

        using CancellationTokenRegistration cancellationRegistration =
            cancellationToken.Register(ReleaseEntry);
        try
        {
            return await computation.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseEntry();
        }
    }

    /// <summary>
    /// Removes results associated with superseded workspace snapshots.
    /// </summary>
    internal void Clear()
    {
        AnalyzerDiagnosticCacheEntry[] entries;
        lock (_versionGate)
        {
            _versions.Clear();
            entries =
            [
                .. _entries
                    .Select(static entry => entry.Value)
            ];
            _entries.Clear();
        }

        foreach (AnalyzerDiagnosticCacheEntry entry in entries)
        {
            entry.Dispose();
        }
    }

    private bool TrySelectVersion(
        long generation,
        (ProjectId ProjectId, VersionStamp Version) key)
    {
        AnalyzerDiagnosticCacheEntry[] retiredEntries;
        lock (_versionGate)
        {
            if (_versions.TryGetValue(
                key.ProjectId,
                out (long Generation, VersionStamp Version) currentVersion))
            {
                if (generation < currentVersion.Generation)
                {
                    return currentVersion.Version == key.Version;
                }

                if (generation == currentVersion.Generation &&
                    currentVersion.Version != key.Version)
                {
                    return false;
                }

                if (currentVersion.Version == key.Version)
                {
                    _versions[key.ProjectId] = (generation, key.Version);
                    return true;
                }
            }

            _versions[key.ProjectId] = (generation, key.Version);
            retiredEntries =
            [
                .. _entries
                    .Where(entry => entry.Key.ProjectId == key.ProjectId &&
                        entry.Key.Version != key.Version)
                    .Where(entry => _entries.TryRemove(entry))
                    .Select(static entry => entry.Value)
            ];
        }

        foreach (AnalyzerDiagnosticCacheEntry entry in retiredEntries)
        {
            entry.Dispose();
        }

        return true;
    }

    private bool IsSelectedVersion(
        (ProjectId ProjectId, VersionStamp Version) key)
    {
        lock (_versionGate)
        {
            return _versions.TryGetValue(
                    key.ProjectId,
                    out (long Generation, VersionStamp Version) currentVersion) &&
                currentVersion.Version == key.Version;
        }
    }

    private void TryRemoveAndDispose(
        (ProjectId ProjectId, VersionStamp Version) key,
        AnalyzerDiagnosticCacheEntry entry)
    {
        if (_entries.TryRemove(new KeyValuePair<
            (ProjectId ProjectId, VersionStamp Version),
            AnalyzerDiagnosticCacheEntry>(key, entry)))
        {
            entry.Dispose();
        }
    }
}

using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using RoslynDiagnostic = Microsoft.CodeAnalysis.Diagnostic;

namespace Csls.Workspaces;

/// <summary>
/// Owns one shared project diagnostic computation and its active request waiters.
/// </summary>
internal sealed class AnalyzerDiagnosticCacheEntry : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Project _project;
    private readonly Func<
        Project,
        CancellationToken,
        Task<ImmutableArray<RoslynDiagnostic>>> _factory;
    private CancellationTokenSource? _computationSource;
    private Task<ImmutableArray<RoslynDiagnostic>>? _computation;
    private int _waiterCount;
    private bool _accepting = true;
    private bool _retired;

    /// <summary>
    /// Creates a cache entry for one immutable Roslyn project snapshot.
    /// </summary>
    /// <param name="project">The immutable Roslyn project snapshot.</param>
    /// <param name="factory">The cancellable project diagnostic computation.</param>
    internal AnalyzerDiagnosticCacheEntry(
        Project project,
        Func<Project, CancellationToken, Task<ImmutableArray<RoslynDiagnostic>>> factory)
    {
        _project = project;
        _factory = factory;
    }

    /// <summary>
    /// Acquires one request waiter and starts the shared computation when required.
    /// </summary>
    /// <param name="computation">The single shared diagnostic computation.</param>
    /// <returns>True when the entry still accepts request waiters.</returns>
    internal bool TryAcquire(
        out Task<ImmutableArray<RoslynDiagnostic>> computation)
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                computation = null!;
                return false;
            }

            _waiterCount++;
            if (_computation is null)
            {
                _computationSource = new CancellationTokenSource();
                CancellationToken computationToken = _computationSource.Token;
                // Roslyn analyzer drivers can execute synchronously before returning a task.
                _computation = Task.Run(() => RunAsync(computationToken));
            }

            computation = _computation;
            return true;
        }
    }

    /// <summary>
    /// Releases one request waiter and cancels work that no request still needs.
    /// </summary>
    /// <returns>True when the cache must remove this entry.</returns>
    internal bool Release()
    {
        CancellationTokenSource? sourceToCancel = null;
        CancellationTokenSource? sourceToDispose = null;
        bool remove;
        lock (_gate)
        {
            if (_waiterCount == 0)
            {
                throw new InvalidOperationException(
                    "A diagnostic cache waiter cannot be released more than once.");
            }

            _waiterCount--;
            bool computationCompleted = _computation?.IsCompleted ?? true;
            if (_waiterCount == 0 && !computationCompleted)
            {
                _accepting = false;
                sourceToCancel = _computationSource;
            }

            remove = !_accepting || _retired;
            if (_waiterCount == 0 && computationCompleted && remove)
            {
                sourceToDispose = DetachComputationSource();
            }
        }

        using (sourceToDispose)
        {
            sourceToCancel?.Cancel();
        }

        return remove;
    }

    /// <summary>
    /// Stops new waiters while preserving work still observed by active requests.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? sourceToCancel = null;
        CancellationTokenSource? sourceToDispose = null;
        lock (_gate)
        {
            _retired = true;
            _accepting = false;
            if (_waiterCount == 0)
            {
                if (_computation?.IsCompleted is false)
                {
                    sourceToCancel = _computationSource;
                }
                else
                {
                    sourceToDispose = DetachComputationSource();
                }
            }
        }

        using (sourceToDispose)
        {
            sourceToCancel?.Cancel();
        }
    }

    private async Task<ImmutableArray<RoslynDiagnostic>> RunAsync(
        CancellationToken cancellationToken)
    {
        bool completedSuccessfully = false;
        try
        {
            ImmutableArray<RoslynDiagnostic> diagnostics = await _factory(
                _project,
                cancellationToken).ConfigureAwait(false);
            completedSuccessfully = true;
            return diagnostics;
        }
        finally
        {
            Finish(completedSuccessfully);
        }
    }

    private void Finish(bool completedSuccessfully)
    {
        CancellationTokenSource? sourceToDispose = null;
        lock (_gate)
        {
            if (!completedSuccessfully)
            {
                _accepting = false;
            }

            if (_waiterCount == 0 && (_retired || !_accepting))
            {
                sourceToDispose = DetachComputationSource();
            }
        }

        sourceToDispose?.Dispose();
    }

    private CancellationTokenSource? DetachComputationSource()
    {
        CancellationTokenSource? source = _computationSource;
        _computationSource = null;
        return source;
    }
}

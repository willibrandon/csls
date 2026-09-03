using Csls.Debugger.Contracts;
using System.Diagnostics;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Lazily owns and recovers the managed evaluator process for one debugger session.
/// </summary>
public sealed class DebuggerEvaluatorSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DebuggerEvaluatorClient? _client;
    private int _disposed;

    /// <summary>
    /// Compiles one expression through the selected compiler-backed language provider.
    /// </summary>
    /// <param name="request">The selected language and source expression.</param>
    /// <param name="cancellationToken">Cancels evaluator startup or compilation.</param>
    /// <returns>The validated language-neutral runtime plan.</returns>
    public async Task<DebugExpressionPlan> CompileAsync(
        DebugExpressionCompileRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    _client ??= await DebuggerEvaluatorClient.StartAsync(cancellationToken)
                        .ConfigureAwait(false);
                    return await _client.CompileAsync(request, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (IOException) when (attempt == 0)
                {
                    if (_client is not null)
                    {
                        await _client.DisposeAsync().ConfigureAwait(false);
                    }

                    _client = null;
                }
            }

            throw new UnreachableException();
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
                _client = null;
            }
        }
        finally
        {
            _ = _gate.Release();
            _gate.Dispose();
        }
    }
}

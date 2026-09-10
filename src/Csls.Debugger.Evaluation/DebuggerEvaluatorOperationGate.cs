using System.Threading.Channels;

namespace Csls.Debugger.Evaluation;

/// <summary>
/// Serializes evaluator operations without owning a disposable synchronization primitive.
/// </summary>
internal sealed class DebuggerEvaluatorOperationGate
{
    private readonly Channel<bool> _token = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

    /// <summary>
    /// Creates an available operation gate.
    /// </summary>
    internal DebuggerEvaluatorOperationGate()
    {
        if (!_token.Writer.TryWrite(true))
        {
            throw new InvalidOperationException("The evaluator operation gate did not initialize.");
        }
    }

    /// <summary>
    /// Waits until the caller exclusively owns evaluator execution.
    /// </summary>
    /// <param name="cancellationToken">Cancels waiting for ownership.</param>
    /// <returns>A task that completes after ownership is acquired.</returns>
    internal async ValueTask EnterAsync(CancellationToken cancellationToken) =>
        _ = await _token.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Returns evaluator execution ownership to the next caller.
    /// </summary>
    internal void Exit()
    {
        if (!_token.Writer.TryWrite(true))
        {
            throw new InvalidOperationException("The evaluator operation gate was released twice.");
        }
    }
}

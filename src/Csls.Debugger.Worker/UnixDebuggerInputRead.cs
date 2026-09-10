namespace Csls.Debugger.Worker;

/// <summary>
/// Retains one caller's buffer until the owned Unix reader completes or cancels its native read.
/// </summary>
/// <param name="Buffer">The writable memory retained until completion.</param>
/// <param name="Completion">The completion that releases the caller's read gate and buffer ownership.</param>
/// <param name="CancellationToken">The caller and stream lifetime cancellation.</param>
internal sealed record UnixDebuggerInputRead(
    Memory<byte> Buffer, TaskCompletionSource<int> Completion, CancellationToken CancellationToken);

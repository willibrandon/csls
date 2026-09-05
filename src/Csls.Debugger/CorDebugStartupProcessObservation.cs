using System.ComponentModel;
using System.Diagnostics;

namespace Csls.Debugger;

/// <summary>
/// Observes target exit during runtime activation and joins the observation before process ownership changes.
/// </summary>
internal sealed class CorDebugStartupProcessObservation : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;

    /// <summary>
    /// Starts observing the target before it resumes or registers its runtime callback.
    /// </summary>
    /// <param name="process">The process retained by the activation owner.</param>
    /// <param name="unixExitMonitor">The exit-status owner for a directly launched Unix child.</param>
    /// <param name="cancellationToken">Cancels the activation request.</param>
    internal CorDebugStartupProcessObservation(
        Process process,
        UnixChildExitMonitor? unixExitMonitor,
        CancellationToken cancellationToken)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Completion = ObserveAsync(process, unixExitMonitor, _cancellation.Token);
    }

    /// <summary>
    /// Gets the target's exit status when the operating system makes it available.
    /// </summary>
    internal Task<int?> Completion { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using (_cancellation)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await Completion.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                Debug.Assert(Completion.IsCompleted);
            }
        }
    }

    private static async Task<int?> ObserveAsync(
        Process process,
        UnixChildExitMonitor? unixExitMonitor,
        CancellationToken cancellationToken)
    {
        if (unixExitMonitor is not null)
        {
            return await unixExitMonitor.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }
}

using System.Diagnostics;

namespace Csls.Server;

public sealed partial class LanguageServer
{
    private CancellationTokenSource? _clientProcessMonitorSource;
    private Task? _clientProcessMonitorTask;

    private void StartClientProcessMonitor(int? processId)
    {
        if (processId is not > 0)
        {
            return;
        }

        _clientProcessMonitorSource = new CancellationTokenSource();
        _clientProcessMonitorTask = MonitorClientProcessAsync(
            processId.Value,
            _clientProcessMonitorSource.Token);
    }

    private async Task MonitorClientProcessAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            InvalidOperationException)
        {
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            await ExitAsync().ConfigureAwait(false);
        }
    }

    private async Task StopClientProcessMonitorAsync()
    {
        CancellationTokenSource? source = _clientProcessMonitorSource;
        Task? monitorTask = _clientProcessMonitorTask;
        _clientProcessMonitorSource = null;
        _clientProcessMonitorTask = null;
        if (source is null)
        {
            return;
        }

        await source.CancelAsync().ConfigureAwait(false);
        if (monitorTask is not null)
        {
            await monitorTask.ConfigureAwait(false);
        }

        source.Dispose();
    }
}

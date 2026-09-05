using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Reports bounded runtime callback failures through debugger console notifications.
/// </summary>
internal sealed partial class CorDebugManagedCallback
{
    private const int MaximumCallbackDiagnosticLength = 8192;

    private async ValueTask ReportCallbackFailureAsync(
        string callbackName,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException ||
            Volatile.Read(ref _detaching) != 0 ||
            cancellationToken.IsCancellationRequested)
        {
            return;
        }

        string details = exception.ToString();
        if (details.Length > MaximumCallbackDiagnosticLength)
        {
            details = details[..MaximumCallbackDiagnosticLength] + " [truncated]";
        }

        try
        {
            await _observer.OnOutputAsync(
                DebugOutputCategory.Console,
                $"Managed debugger callback '{callbackName}' failed: {details}{Environment.NewLine}",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception transportException) when (
            transportException is IOException or InvalidOperationException or
                OperationCanceledException or System.Threading.Channels.ChannelClosedException)
        {
            // A closed session transport cannot accept diagnostic notifications.
            System.Diagnostics.Debug.WriteLine(transportException);
        }
    }
}

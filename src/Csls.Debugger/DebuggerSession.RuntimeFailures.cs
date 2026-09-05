using Csls.Debugger.Contracts;

namespace Csls.Debugger;

/// <summary>
/// Ends failed debugging sessions while preserving target process ownership.
/// </summary>
public sealed partial class DebuggerSession
{
    private async Task HandleRuntimeFailureAsync(
        CorDebugDebuggee debuggee,
        CorDebugRuntimeException failure,
        Task standardOutput,
        Task standardError)
    {
        try
        {
            await _actor.InvokeAsync(
                async token =>
                {
                    _state = DebugSessionState.Faulted;
                    _stopGeneration = _stopGeneration.Value == 0
                        ? DebugStopGeneration.First
                        : _stopGeneration.Next();
                    _currentException = null;
                    _currentExceptionThreadId = null;
                    debuggee.AbandonFailedRuntime(failure);
                    string cleanup = debuggee.OwnsProcess
                        ? "The debugger is terminating the launched target through the operating system."
                        : "The attached target will not be terminated by the debugger.";
                    await _observer.OnOutputAsync(
                        DebugOutputCategory.Console,
                        $"{failure.Message} {cleanup}{Environment.NewLine}",
                        token).ConfigureAwait(false);
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception transportException) when (
            IsRuntimeFailureTransportException(transportException))
        {
            System.Diagnostics.Debug.WriteLine(transportException);
        }
        finally
        {
            await CompleteRuntimeFailureCleanupAsync(debuggee, standardOutput, standardError)
                .WaitAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task CompleteRuntimeFailureCleanupAsync(
        CorDebugDebuggee debuggee,
        Task standardOutput,
        Task standardError)
    {
        try
        {
            if (debuggee.OwnsProcess)
            {
                await debuggee.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await Task.WhenAll(standardOutput, standardError)
                    .WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception transportException) when (
                IsRuntimeFailureTransportException(transportException))
            {
                System.Diagnostics.Debug.WriteLine(transportException);
            }

            try
            {
                await _actor.InvokeAsync(
                    token => _observer.OnTerminatedAsync(token),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception transportException) when (
                IsRuntimeFailureTransportException(transportException))
            {
                System.Diagnostics.Debug.WriteLine(transportException);
            }
        }
    }

    private static bool IsRuntimeFailureTransportException(Exception exception) =>
        exception is IOException or OperationCanceledException or
            System.Threading.Channels.ChannelClosedException;
}

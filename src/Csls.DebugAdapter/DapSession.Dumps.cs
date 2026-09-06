using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;

namespace Csls.DebugAdapter;

/// <summary>
/// Coordinates offline dump activation, inspection ownership, and worker termination.
/// </summary>
internal sealed partial class DapSession
{
    private async Task OpenDumpAsync(Request configurationRequest, Request attachRequest,
        DebugDumpOpenRequest options, CancellationToken cancellationToken)
    {
        DapDumpSession dump = await DapDumpSession.OpenAsync(options, _writeErrorAsync, cancellationToken)
            .ConfigureAwait(false);
        _dumpSession = dump;
        _inspectionTarget = dump;
        _activeTargetArguments = _pendingTargetArguments;
        ClearPendingTarget();
        _state = DapSessionState.Stopped;
        _stoppedThreadId = dump.Snapshot.StoppedThreadId;
        // Activation has committed; later request cancellation cannot retract its successful responses.
        await _writer.WriteResponseAsync(configurationRequest, success: true, message: null, writeBody: null,
            _lifetime.Token).ConfigureAwait(false);
        await _writer.WriteResponseAsync(attachRequest, success: true, message: null, writeBody: null,
            _lifetime.Token).ConfigureAwait(false);
        await WriteStoppedEventAsync("dump", _stoppedThreadId, exception: null, _lifetime.Token)
            .ConfigureAwait(false);
    }

    private async Task DisposeDumpSessionAsync()
    {
        if (_dumpSession is not DapDumpSession dump)
        {
            return;
        }

        _dumpSession = null;
        _inspectionTarget = _engineSession;
        try
        {
            await dump.DisposeAsync().ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            await _writeErrorAsync(exception.Message).ConfigureAwait(false);
        }
    }

    private async Task HandleDumpWorkerExitAsync(CancellationToken cancellationToken)
    {
        _state = DapSessionState.Faulted;
        if (_cancelableRequestCancellation is not null)
        {
            await _cancelableRequestCancellation.CancelAsync().ConfigureAwait(false);
            await CompleteCancelableRequestAsync().ConfigureAwait(false);
        }
        await DisposeDumpSessionAsync().ConfigureAwait(false);
        await _writeErrorAsync("The managed dump worker exited before its session was closed.").ConfigureAwait(false);
        await _writer.WriteEventAsync("terminated", writeBody: null, cancellationToken).ConfigureAwait(false);
    }
}

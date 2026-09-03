using Csls.DebugAdapter.Protocol;
using System.ComponentModel;

namespace Csls.DebugAdapter;

/// <summary>
/// Coordinates deferred DAP launch and attach activation.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask PrepareLaunchAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Initialized)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            _pendingLaunch = DapLaunchOptionsParser.Parse(request.Arguments);
            await _engineSession.ConfigureSourceOptionsAsync(
                _pendingLaunch.Options.SourceFileMap,
                _pendingLaunch.Options.SourceLinkOptions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        _pendingTargetRequest = request;
        _pendingAttachProcessId = null;
        _startMethod = "launch";
        _terminateDebuggeeByDefault = true;
        _state = DapSessionState.Configuring;
        await _writer.WriteEventAsync(
            "initialized",
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteTargetStartAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Configuring ||
            _pendingTargetRequest is null ||
            (_pendingLaunch is null) == (_pendingAttachProcessId is null))
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = DapSessionState.Starting;
        _pendingConfigurationRequest = request;
        try
        {
            if (_pendingAttachProcessId is int processId)
            {
                await _engineSession
                    .AttachManagedAsync(processId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (_pendingLaunch!.NoDebug)
            {
                await _engineSession
                    .LaunchWithoutDebuggingAsync(_pendingLaunch.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _engineSession
                    .LaunchManagedAsync(_pendingLaunch.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or
                UnauthorizedAccessException or Win32Exception)
        {
            _state = DapSessionState.Initialized;
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                _pendingTargetRequest,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            ClearPendingTarget();
        }
    }
}

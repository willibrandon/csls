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
        _pendingTargetArguments = request.Arguments.Clone();
        _pendingAttach = null;
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
        Request? targetRequest = _pendingTargetRequest;
        DapLaunchConfiguration? launch = _pendingLaunch;
        DapAttachConfiguration? attach = _pendingAttach;
        if (_state != DapSessionState.Configuring ||
            targetRequest is null ||
            (launch is null) == (attach is null))
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = DapSessionState.Starting;
        _pendingConfigurationRequest = request;
        try
        {
            if (attach is not null)
            {
                await _engineSession
                    .AttachManagedAsync(attach.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (launch is not null && launch.NoDebug)
            {
                await _engineSession
                    .LaunchWithoutDebuggingAsync(launch.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _engineSession
                    .LaunchManagedAsync(
                        launch?.Options ?? throw new InvalidOperationException(
                            "The pending launch configuration is unavailable."),
                        cancellationToken)
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
                targetRequest,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            ClearPendingTarget();
        }
    }
}

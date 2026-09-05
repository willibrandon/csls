using Csls.DebugAdapter.Protocol;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Restarts the active DAP target without replacing the adapter connection.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask RestartAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state is not DapSessionState.Running and not DapSessionState.Stopped ||
            _activeTargetArguments is null)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonElement arguments = _activeTargetArguments.Value;
        if (request.Arguments.ValueKind == JsonValueKind.Object &&
            request.Arguments.TryGetProperty("arguments", out JsonElement replacement))
        {
            if (replacement.ValueKind != JsonValueKind.Object)
            {
                await WriteRequestFailureAsync(
                    request,
                    "The restart arguments value must be an object.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            arguments = replacement;
        }

        DapAttachConfiguration? attach = null;
        DapLaunchConfiguration? launch = null;
        try
        {
            if (string.Equals(_startMethod, "attach", StringComparison.Ordinal))
            {
                attach = DapAttachOptionsParser.Parse(arguments);
            }
            else
            {
                launch = DapLaunchOptionsParser.Parse(arguments);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
                Win32Exception)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        _restartRequest = request;
        _restartTargetArguments = arguments.Clone();
        _isRestarting = true;
        _state = DapSessionState.Starting;
        try
        {
            if (attach is not null)
            {
                await _engineSession.RestartManagedAttachAsync(
                    attach.Options,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                DapLaunchConfiguration launchConfiguration = launch
                    ?? throw new InvalidOperationException(
                        "The active launch configuration is unavailable.");
                if (launchConfiguration.NoDebug)
                {
                    await _engineSession.RestartWithoutDebuggingAsync(
                        launchConfiguration.Options,
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _engineSession.RestartManagedAsync(
                        launchConfiguration.Options,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException or
                Win32Exception)
        {
            _restartRequest = null;
            _restartTargetArguments = null;
            _isRestarting = false;
            _state = DapSessionState.Terminated;
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteEventAsync(
                "terminated",
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
    }
}

using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles DAP execution control and target disconnection.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask PauseAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Running)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            _stoppedThreadId = GetOptionalPositiveInteger(
                request.Arguments,
                "threadId",
                request.Command);
            _deferredStoppedReason = "pause";
            await _engineSession.PauseAsync(cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await FlushDeferredStopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _deferredStoppedReason = null;
            _deferredStop = null;
        }
    }

    private async ValueTask ContinueAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _engineSession.ContinueAsync(cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writeBody: writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("allThreadsContinued", true);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask StepAsync(
        Request request,
        DebugStepKind kind,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            int threadId = GetRequiredInteger(request.Arguments, "threadId", request.Command);
            int? targetId = kind == DebugStepKind.Into &&
                request.Arguments.TryGetProperty("targetId", out JsonElement targetValue)
                ? targetValue.TryGetInt32(out int parsedTarget)
                    ? parsedTarget
                    : throw new ArgumentException("The stepIn targetId must be an integer.")
                : null;
            await _engineSession
                .StepAsync(threadId, kind, targetId, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask DisconnectAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        bool terminateDebuggee = _terminateDebuggeeByDefault;
        if (request.Arguments.ValueKind == JsonValueKind.Object &&
            request.Arguments.TryGetProperty("terminateDebuggee", out JsonElement terminateValue))
        {
            if (terminateValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                await WriteRequestFailureAsync(
                    request,
                    "The disconnect terminateDebuggee value must be boolean.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            terminateDebuggee = terminateValue.GetBoolean();
        }

        _state = DapSessionState.Terminating;
        if (terminateDebuggee)
        {
            await _engineSession.TerminateAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _engineSession.DetachAsync(cancellationToken).ConfigureAwait(false);
        }

        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
        _state = DapSessionState.Terminated;
    }
}

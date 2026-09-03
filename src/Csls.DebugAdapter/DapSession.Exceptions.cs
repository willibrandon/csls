using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles DAP managed exception policy and current exception inspection.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteExceptionInfoAsync(
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
            int threadId = GetRequiredInteger(request.Arguments, "threadId", "exceptionInfo");
            DebugExceptionInfo exception = await _engineSession
                .GetExceptionInfoAsync(threadId, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("exceptionId", exception.ExceptionId);
                    writer.WriteString("description", exception.Description);
                    writer.WriteString("breakMode", exception.BreakMode switch
                    {
                        DebugExceptionBreakMode.Thrown => "always",
                        DebugExceptionBreakMode.UserUnhandled => "userUnhandled",
                        DebugExceptionBreakMode.Unhandled => "unhandled",
                        _ => throw new InvalidOperationException(
                            "The debugger returned an unknown exception break mode.")
                    });
                    writer.WriteStartObject("details");
                    writer.WriteString("typeName", exception.ExceptionId);
                    writer.WriteString("message", exception.Description);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
                IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

}

using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles DAP managed stack-trace inspection.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteStackTraceAsync(
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
            JsonElement arguments = request.Arguments;
            if (arguments.ValueKind != JsonValueKind.Object ||
                !arguments.TryGetProperty("threadId", out JsonElement threadIdValue) ||
                !threadIdValue.TryGetInt32(out int threadId))
            {
                throw new ArgumentException(
                    "The stackTrace request requires an integer threadId.");
            }

            int startFrame = GetOptionalNonNegativeInteger(arguments, "startFrame", "stackTrace");
            int levels = GetOptionalNonNegativeInteger(arguments, "levels", "stackTrace");
            DebugStackTrace stack = await _engineSession.GetStackTraceAsync(
                threadId,
                startFrame,
                levels,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer => WriteStackTrace(writer, stack),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void WriteStackTrace(Utf8JsonWriter writer, DebugStackTrace stack)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("stackFrames");
        foreach (DebugStackFrameInfo frame in stack.StackFrames)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", frame.Id);
            writer.WriteString("name", frame.Name);
            if (frame.Source is not null)
            {
                writer.WritePropertyName("source");
                WriteSource(writer, frame.Source);
            }

            writer.WriteNumber("line", ToClientLine(frame.Line));
            writer.WriteNumber("column", ToClientColumn(frame.Column));
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteNumber("totalFrames", stack.TotalFrames);
        writer.WriteEndObject();
    }
}

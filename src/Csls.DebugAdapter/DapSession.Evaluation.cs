using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles stopped-frame expression evaluation for DAP clients.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask EvaluateAsync(
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
            string expression = GetRequiredNonEmptyString(
                request.Arguments,
                "expression",
                "evaluate");
            int frameId = await GetEvaluationFrameIdAsync(
                request.Arguments,
                cancellationToken).ConfigureAwait(false);
            DebugEvaluateResult result = await _engineSession.EvaluateAsync(
                frameId,
                expression,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("result", result.Result);
                    writer.WriteString("type", result.Type);
                    writer.WriteNumber("variablesReference", result.VariablesReference);
                    if (result.MemoryReference is not null)
                    {
                        writer.WriteString("memoryReference", result.MemoryReference);
                    }

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

    private async Task<int> GetEvaluationFrameIdAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("frameId", out JsonElement frameIdElement))
        {
            if (!frameIdElement.TryGetInt32(out int frameId) || frameId <= 0)
            {
                throw new ArgumentException(
                    "The evaluate frameId value must be a positive integer.");
            }

            return frameId;
        }

        int threadId = _stoppedThreadId ?? throw new InvalidOperationException(
            "The evaluate request requires frameId when no stopped thread is selected.");
        DebugStackTrace stack = await _engineSession.GetStackTraceAsync(
            threadId,
            startFrame: 0,
            levels: 1,
            cancellationToken).ConfigureAwait(false);
        if (stack.StackFrames.Count == 0)
        {
            throw new InvalidOperationException(
                "The stopped thread has no managed frame available for evaluation.");
        }

        return stack.StackFrames[0].Id;
    }
}

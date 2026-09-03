using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles explicit stopped-state variable and source-expression assignments.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask SetVariableAsync(
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
            int variablesReference = GetRequiredInteger(
                arguments,
                "variablesReference",
                "setVariable");
            string name = GetRequiredNonEmptyString(arguments, "name", "setVariable");
            string value = GetRequiredNonEmptyString(arguments, "value", "setVariable");
            DebugVariableInfo result = await _engineSession.SetVariableAsync(
                variablesReference,
                name,
                value,
                _engineSession.StopGeneration,
                cancellationToken).ConfigureAwait(false);
            await WriteAssignmentResponseAsync(request, result, cancellationToken)
                .ConfigureAwait(false);
            await WriteVariableInvalidationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask SetExpressionAsync(
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
            string expression = GetRequiredNonEmptyString(
                arguments,
                "expression",
                "setExpression");
            string value = GetRequiredNonEmptyString(arguments, "value", "setExpression");
            int frameId = await GetEvaluationFrameIdAsync(arguments, cancellationToken)
                .ConfigureAwait(false);
            DebugVariableInfo result = await _engineSession.SetExpressionAsync(
                frameId,
                expression,
                value,
                _engineSession.StopGeneration,
                cancellationToken).ConfigureAwait(false);
            await WriteAssignmentResponseAsync(request, result, cancellationToken)
                .ConfigureAwait(false);
            await WriteVariableInvalidationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private ValueTask WriteAssignmentResponseAsync(
        Request request,
        DebugVariableInfo result,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("value", result.Value);
                writer.WriteString("type", result.Type);
                writer.WriteNumber("variablesReference", result.VariablesReference);
                if (result.MemoryReference is not null)
                {
                    writer.WriteString("memoryReference", result.MemoryReference);
                }

                writer.WriteEndObject();
            },
            cancellationToken);

    private ValueTask WriteVariableInvalidationAsync(CancellationToken cancellationToken) =>
        _writer.WriteEventAsync(
            "invalidated",
            static writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("areas");
                writer.WriteStringValue("variables");
                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);
}

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

        DebugStopGeneration requestGeneration = _engineSession.StopGeneration;
        bool variableInvalidation = false;
        bool targetCodeExecuted = false;
        try
        {
            JsonElement arguments = request.Arguments;
            int variablesReference = GetRequiredInteger(
                arguments,
                "variablesReference",
                "setVariable");
            string name = GetRequiredNonEmptyString(arguments, "name", "setVariable");
            string value = GetRequiredNonEmptyString(arguments, "value", "setVariable");
            DebugAssignmentResult result = await _engineSession.SetVariableAsync(
                variablesReference,
                name,
                value,
                _engineSession.StopGeneration,
                cancellationToken).ConfigureAwait(false);
            SignalCancelableResponseReady();
            await WriteAssignmentResponseAsync(request, result.Variable, cancellationToken)
                .ConfigureAwait(false);
            variableInvalidation = true;
            targetCodeExecuted = result.TargetCodeExecuted;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            SignalCancelableResponseReady();
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SignalCancelableResponseReady();
            await WriteRequestFailureAsync(request, "cancelled", _lifetime.Token)
                .ConfigureAwait(false);
        }

        targetCodeExecuted |= _engineSession.StopGeneration != requestGeneration;
        if (variableInvalidation || targetCodeExecuted)
        {
            await WriteAssignmentInvalidationAsync(
                targetCodeExecuted,
                _lifetime.Token).ConfigureAwait(false);
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

        DebugStopGeneration requestGeneration = _engineSession.StopGeneration;
        bool variableInvalidation = false;
        bool targetCodeExecuted = false;
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
            DebugAssignmentResult result = await _engineSession.SetExpressionAsync(
                frameId,
                expression,
                value,
                _engineSession.StopGeneration,
                cancellationToken).ConfigureAwait(false);
            SignalCancelableResponseReady();
            await WriteAssignmentResponseAsync(request, result.Variable, cancellationToken)
                .ConfigureAwait(false);
            variableInvalidation = true;
            targetCodeExecuted = result.TargetCodeExecuted;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            SignalCancelableResponseReady();
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SignalCancelableResponseReady();
            await WriteRequestFailureAsync(request, "cancelled", _lifetime.Token)
                .ConfigureAwait(false);
        }

        targetCodeExecuted |= _engineSession.StopGeneration != requestGeneration;
        if (variableInvalidation || targetCodeExecuted)
        {
            await WriteAssignmentInvalidationAsync(
                targetCodeExecuted,
                _lifetime.Token).ConfigureAwait(false);
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

    private ValueTask WriteAssignmentInvalidationAsync(
        bool targetCodeExecuted,
        CancellationToken cancellationToken) =>
        _writer.WriteEventAsync(
            "invalidated",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("areas");
                if (targetCodeExecuted)
                {
                    writer.WriteStringValue("stacks");
                }

                writer.WriteStringValue("variables");
                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);
}

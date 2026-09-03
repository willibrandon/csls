using Csls.DebugAdapter.Protocol;
using Csls.Debugger.Contracts;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Handles DAP thread, module, stack, scope, and variable inspection.
/// </summary>
internal sealed partial class DapSession
{
    private async ValueTask WriteThreadsAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state is not DapSessionState.Running and not DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_state == DapSessionState.Running)
        {
            await WriteThreadsResponseAsync(
                request,
                [],
                cancellationToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<DebugThreadInfo> threads;
        try
        {
            threads = await _engineSession.GetThreadsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteThreadsResponseAsync(request, threads, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask WriteThreadsResponseAsync(
        Request request,
        IReadOnlyList<DebugThreadInfo> threads,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartArray("threads");
                foreach (DebugThreadInfo thread in threads)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", thread.Id);
                    writer.WriteString("name", thread.Name);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            cancellationToken);

    private async ValueTask WriteScopesAsync(
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
            int frameId = GetRequiredInteger(request.Arguments, "frameId", "scopes");
            IReadOnlyList<DebugScopeInfo> scopes = await _engineSession
                .GetScopesAsync(frameId, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("scopes");
                    foreach (DebugScopeInfo scope in scopes)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", scope.Name);
                        writer.WriteNumber("variablesReference", scope.VariablesReference);
                        writer.WriteBoolean("expensive", scope.Expensive);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask WriteVariablesAsync(
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
                "variables");
            int start = GetOptionalNonNegativeInteger(arguments, "start", "variables");
            int count = GetOptionalNonNegativeInteger(arguments, "count", "variables");
            IReadOnlyList<DebugVariableInfo> variables = await _engineSession
                .GetVariablesAsync(
                    variablesReference,
                    start,
                    count,
                    cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("variables");
                    foreach (DebugVariableInfo variable in variables)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", variable.Name);
                        writer.WriteString("value", variable.Value);
                        writer.WriteString("type", variable.Type);
                        writer.WriteNumber("variablesReference", variable.VariablesReference);
                        if (variable.MemoryReference is not null)
                        {
                            writer.WriteString("memoryReference", variable.MemoryReference);
                        }

                        if (variable.EvaluateName is not null)
                        {
                            writer.WriteString("evaluateName", variable.EvaluateName);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
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

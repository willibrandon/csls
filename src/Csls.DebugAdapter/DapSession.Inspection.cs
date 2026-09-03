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

    private async ValueTask WriteModulesAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state is not DapSessionState.Running and not DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            int startModule = GetOptionalNonNegativeInteger(
                request.Arguments,
                "startModule",
                "modules");
            int moduleCount = GetOptionalNonNegativeInteger(
                request.Arguments,
                "moduleCount",
                "modules");
            DebugModulePage page = await _engineSession
                .GetModulesAsync(startModule, moduleCount, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("modules");
                    foreach (DebugModuleInfo module in page.Modules)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("id", module.Id);
                        writer.WriteString("name", module.Name);
                        if (module.Path is not null)
                        {
                            writer.WriteString("path", module.Path);
                        }

                        writer.WriteString(
                            "symbolStatus",
                            module.SymbolPath is null ? "Symbols not found." : "Symbols loaded.");
                        if (module.SymbolPath is not null)
                        {
                            writer.WriteString("symbolFilePath", module.SymbolPath);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteNumber("totalModules", page.TotalModules);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await WriteRequestFailureAsync(request, exception.Message, cancellationToken)
                .ConfigureAwait(false);
        }
    }

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
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("stackFrames");
                    foreach (DebugStackFrameInfo frame in stack.StackFrames)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("id", frame.Id);
                        writer.WriteString("name", frame.Name);
                        if (frame.SourcePath is not null)
                        {
                            writer.WriteStartObject("source");
                            writer.WriteString("name", Path.GetFileName(frame.SourcePath));
                            writer.WriteString("path", frame.SourcePath);
                            writer.WriteEndObject();
                        }

                        writer.WriteNumber("line", frame.Line);
                        writer.WriteNumber("column", frame.Column);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteNumber("totalFrames", stack.TotalFrames);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

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

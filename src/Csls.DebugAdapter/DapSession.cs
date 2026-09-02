using Csls.DebugAdapter.Protocol;
using Csls.Debugger;
using Csls.Debugger.Contracts;
using System.ComponentModel;
using System.Text.Json;

namespace Csls.DebugAdapter;

/// <summary>
/// Translates one Debug Adapter Protocol connection to a protocol-neutral debugger session.
/// </summary>
internal sealed class DapSession : IDebuggerSessionObserver, IAsyncDisposable
{
    private readonly DapMessageReader _reader;
    private readonly DapMessageWriter _writer;
    private readonly Func<string, Task> _writeErrorAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DebuggerSession _engineSession;
    private DapSessionState _state = DapSessionState.Created;
    private Request? _pendingLaunchRequest;
    private Request? _pendingConfigurationRequest;
    private DapLaunchConfiguration? _pendingLaunch;
    private int _protocolClosed;

    /// <summary>
    /// Creates a DAP session over explicit protocol and diagnostic streams.
    /// </summary>
    /// <param name="input">The protocol input stream.</param>
    /// <param name="output">The protocol output stream.</param>
    /// <param name="error">The diagnostics-only text stream.</param>
    internal DapSession(Stream input, Stream output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        _reader = new DapMessageReader(input);
        _writer = new DapMessageWriter(output);
        _writeErrorAsync = error.WriteLineAsync;
        _engineSession = DebuggerEngine.CreateSession(this);
    }

    /// <summary>
    /// Processes requests until disconnect, end of input, cancellation, or protocol failure.
    /// </summary>
    /// <param name="cancellationToken">Cancels the complete adapter session.</param>
    /// <returns>Zero for a normal session or one for a terminal protocol failure.</returns>
    internal async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        try
        {
            while (_state is not DapSessionState.Terminated and not DapSessionState.Faulted)
            {
                Request? request = await _reader
                    .ReadRequestAsync(linked.Token)
                    .ConfigureAwait(false);
                if (request is null)
                {
                    break;
                }

                await HandleRequestAsync(request, linked.Token).ConfigureAwait(false);
            }

            return _state == DapSessionState.Faulted ? 1 : 0;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return 0;
        }
        catch (InvalidDataException exception)
        {
            _state = DapSessionState.Faulted;
            await _writeErrorAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Volatile.Write(ref _protocolClosed, 1);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _engineSession.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        await _writer.DisposeAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask OnProcessStartedAsync(
        string name,
        int processId,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        if (_pendingConfigurationRequest is null || _pendingLaunchRequest is null)
        {
            throw new InvalidOperationException(
                "The engine reported a process before DAP launch configuration completed.");
        }

        try
        {
            _state = DapSessionState.Running;
            await _writer.WriteResponseAsync(
                _pendingConfigurationRequest,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                _pendingLaunchRequest,
                success: true,
                message: null,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteEventAsync(
                "process",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", name);
                    writer.WriteNumber("systemProcessId", processId);
                    writer.WriteBoolean("isLocalProcess", true);
                    writer.WriteString("startMethod", "launch");
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
            ClearPendingLaunch();
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnOutputAsync(
        DebugOutputCategory category,
        string output,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "output",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString(
                        "category",
                        category == DebugOutputCategory.StandardOutput ? "stdout" : "stderr");
                    writer.WriteString("output", output);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnStoppedAsync(
        string reason,
        int? threadId,
        DebugStopGeneration generation,
        CancellationToken cancellationToken)
    {
        _ = generation;
        if (IsProtocolClosed)
        {
            return;
        }

        _state = DapSessionState.Stopped;
        try
        {
            await _writer.WriteEventAsync(
                "stopped",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", reason);
                    if (threadId is not null)
                    {
                        writer.WriteNumber("threadId", threadId.Value);
                    }

                    writer.WriteBoolean("allThreadsStopped", true);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnBreakpointChangedAsync(
        DebugSourceBreakpointInfo breakpoint,
        CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "breakpoint",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("reason", "changed");
                    writer.WritePropertyName("breakpoint");
                    WriteBreakpoint(writer, breakpoint);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnContinuedAsync(CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        _state = DapSessionState.Running;
        try
        {
            await _writer.WriteEventAsync(
                "continued",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("allThreadsContinued", true);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnExitedAsync(int exitCode, CancellationToken cancellationToken)
    {
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "exited",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("exitCode", exitCode);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask OnTerminatedAsync(CancellationToken cancellationToken)
    {
        bool endedWithoutClientRequest = _state == DapSessionState.Running;
        _state = DapSessionState.Terminated;
        if (IsProtocolClosed)
        {
            return;
        }

        try
        {
            await _writer.WriteEventAsync(
                "terminated",
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsExpectedClosedTransportException(exception))
        {
            return;
        }

        if (endedWithoutClientRequest)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask HandleRequestAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        switch (request.Command)
        {
            case "initialize":
                await InitializeAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "launch":
                await PrepareLaunchAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "configurationDone":
                await CompleteLaunchAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "setBreakpoints":
                await SetBreakpointsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "threads":
                await WriteThreadsAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "pause":
                await PauseAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "continue":
                await ContinueAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "next":
                await StepAsync(request, DebugStepKind.Over, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stepIn":
                await StepAsync(request, DebugStepKind.Into, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stepOut":
                await StepAsync(request, DebugStepKind.Out, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case "stackTrace":
                await WriteStackTraceAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "scopes":
                await WriteScopesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "variables":
                await WriteVariablesAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "disconnect":
                await DisconnectAsync(request, cancellationToken).ConfigureAwait(false);
                break;
            case "cancel":
                await _writer.WriteResponseAsync(
                    request,
                    success: true,
                    message: null,
                    writeBody: null,
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                await _writer.WriteResponseAsync(
                    request,
                    success: false,
                    $"The request '{request.Command}' is not supported by this debugger capability set.",
                    writeBody: null,
                    cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask InitializeAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Created)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = DapSessionState.Initialized;
        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: static writer =>
            {
                writer.WriteStartObject();
                writer.WriteBoolean("supportsConfigurationDoneRequest", true);
                writer.WriteBoolean("supportsVariablePaging", true);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
    }

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

        _pendingLaunchRequest = request;
        _state = DapSessionState.Configuring;
        await _writer.WriteEventAsync(
            "initialized",
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CompleteLaunchAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Configuring ||
            _pendingLaunchRequest is null ||
            _pendingLaunch is null)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        _state = DapSessionState.Starting;
        _pendingConfigurationRequest = request;
        try
        {
            if (_pendingLaunch.NoDebug)
            {
                await _engineSession
                    .LaunchWithoutDebuggingAsync(_pendingLaunch.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await _engineSession
                    .LaunchManagedAsync(_pendingLaunch.Options, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            Win32Exception)
        {
            _state = DapSessionState.Initialized;
            await _writer.WriteResponseAsync(
                request,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                _pendingLaunchRequest,
                success: false,
                exception.Message,
                writeBody: null,
                cancellationToken).ConfigureAwait(false);
            ClearPendingLaunch();
        }
    }

    private async ValueTask SetBreakpointsAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state is not DapSessionState.Configuring and not DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            (string sourcePath, IReadOnlyList<DebugSourceBreakpointRequest> breakpoints) =
                ParseSourceBreakpoints(request.Arguments);
            IReadOnlyList<DebugSourceBreakpointInfo> results = await _engineSession
                .SetSourceBreakpointsAsync(sourcePath, breakpoints, cancellationToken)
                .ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("breakpoints");
                    foreach (DebugSourceBreakpointInfo breakpoint in results)
                    {
                        WriteBreakpoint(writer, breakpoint);
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

    private static (string SourcePath, IReadOnlyList<DebugSourceBreakpointRequest> Breakpoints)
        ParseSourceBreakpoints(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("source", out JsonElement source) ||
            source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty("path", out JsonElement pathValue) ||
            pathValue.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(pathValue.GetString()))
        {
            throw new ArgumentException(
                "The setBreakpoints request requires an absolute source.path.");
        }

        if (arguments.TryGetProperty("sourceModified", out JsonElement sourceModified) &&
            sourceModified.ValueKind == JsonValueKind.True)
        {
            throw new ArgumentException(
                "Breakpoints cannot be bound while the editor source differs from the saved file.");
        }

        var result = new List<DebugSourceBreakpointRequest>();
        if (arguments.TryGetProperty("breakpoints", out JsonElement breakpoints))
        {
            if (breakpoints.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException("The setBreakpoints breakpoints value must be an array.");
            }

            foreach (JsonElement breakpoint in breakpoints.EnumerateArray())
            {
                if (breakpoint.ValueKind != JsonValueKind.Object ||
                    !breakpoint.TryGetProperty("line", out JsonElement lineValue) ||
                    !lineValue.TryGetInt32(out int line))
                {
                    throw new ArgumentException(
                        "Every source breakpoint requires an integer line.");
                }

                RejectUnsupportedBreakpointOption(breakpoint, "condition");
                RejectUnsupportedBreakpointOption(breakpoint, "hitCondition");
                RejectUnsupportedBreakpointOption(breakpoint, "logMessage");
                int? column = null;
                if (breakpoint.TryGetProperty("column", out JsonElement columnValue))
                {
                    if (!columnValue.TryGetInt32(out int parsedColumn))
                    {
                        throw new ArgumentException(
                            "A source breakpoint column must be an integer.");
                    }

                    column = parsedColumn;
                }

                result.Add(new DebugSourceBreakpointRequest(line, column));
            }
        }
        else if (arguments.TryGetProperty("lines", out JsonElement lines) &&
            lines.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement lineValue in lines.EnumerateArray())
            {
                if (!lineValue.TryGetInt32(out int line))
                {
                    throw new ArgumentException(
                        "Every setBreakpoints lines entry must be an integer.");
                }

                result.Add(new DebugSourceBreakpointRequest(line, Column: null));
            }
        }

        return (pathValue.GetString()!, result);
    }

    private static void RejectUnsupportedBreakpointOption(
        JsonElement breakpoint,
        string propertyName)
    {
        if (breakpoint.TryGetProperty(propertyName, out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrEmpty(value.GetString()))
        {
            throw new ArgumentException(
                $"Source breakpoint {propertyName} is not supported by this capability set.");
        }
    }

    private static void WriteBreakpoint(
        Utf8JsonWriter writer,
        DebugSourceBreakpointInfo breakpoint)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", breakpoint.Id);
        writer.WriteBoolean("verified", breakpoint.Verified);
        writer.WriteStartObject("source");
        writer.WriteString("name", Path.GetFileName(breakpoint.SourcePath));
        writer.WriteString("path", breakpoint.SourcePath);
        writer.WriteEndObject();
        writer.WriteNumber("line", breakpoint.Line);
        if (breakpoint.Column is not null)
        {
            writer.WriteNumber("column", breakpoint.Column.Value);
        }

        if (breakpoint.Message is not null)
        {
            writer.WriteString("message", breakpoint.Message);
        }

        writer.WriteEndObject();
    }

    private async ValueTask WriteThreadsAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        if (_state != DapSessionState.Stopped)
        {
            await WriteStateFailureAsync(request, cancellationToken).ConfigureAwait(false);
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

        await _writer.WriteResponseAsync(
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
            cancellationToken).ConfigureAwait(false);
    }

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
            await _engineSession.PauseAsync(cancellationToken).ConfigureAwait(false);
            await _writer.WriteResponseAsync(
                request,
                success: true,
                message: null,
                writeBody: null,
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
            await _engineSession
                .StepAsync(threadId, kind, cancellationToken)
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

            int startFrame = GetOptionalNonNegativeInteger(arguments, "startFrame");
            int levels = GetOptionalNonNegativeInteger(arguments, "levels");
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
            int start = GetOptionalNonNegativeInteger(arguments, "start");
            int count = GetOptionalNonNegativeInteger(arguments, "count");
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

    private static int GetOptionalNonNegativeInteger(
        JsonElement arguments,
        string propertyName)
    {
        if (!arguments.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        if (!value.TryGetInt32(out int result) || result < 0)
        {
            throw new ArgumentException(
                $"The stackTrace {propertyName} value must be a non-negative integer.");
        }

        return result;
    }

    private static int GetRequiredInteger(
        JsonElement arguments,
        string propertyName,
        string requestName)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(propertyName, out JsonElement value) ||
            !value.TryGetInt32(out int result))
        {
            throw new ArgumentException(
                $"The {requestName} request requires an integer {propertyName}.");
        }

        return result;
    }

    private ValueTask WriteRequestFailureAsync(
        Request request,
        string message,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: false,
            message,
            writeBody: null,
            cancellationToken);

    private async ValueTask DisconnectAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        _state = DapSessionState.Terminating;
        await _engineSession.TerminateAsync(cancellationToken).ConfigureAwait(false);
        await _writer.WriteResponseAsync(
            request,
            success: true,
            message: null,
            writeBody: null,
            cancellationToken).ConfigureAwait(false);
        _state = DapSessionState.Terminated;
    }

    private ValueTask WriteStateFailureAsync(
        Request request,
        CancellationToken cancellationToken) =>
        _writer.WriteResponseAsync(
            request,
            success: false,
            $"The request '{request.Command}' is invalid while the session is {_state}.",
            writeBody: null,
            cancellationToken);

    private void ClearPendingLaunch()
    {
        _pendingLaunchRequest = null;
        _pendingConfigurationRequest = null;
        _pendingLaunch = null;
    }

    private bool IsProtocolClosed => Volatile.Read(ref _protocolClosed) != 0;

    private bool IsExpectedClosedTransportException(Exception exception) =>
        IsProtocolClosed && exception is IOException or ObjectDisposedException or OperationCanceledException;
}

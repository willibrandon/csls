using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Measures one published csls launcher through its production LSP streams.
/// </summary>
internal sealed class LanguageServerMeasurementSession : IAsyncDisposable
{
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_resourceSettleTime = TimeSpan.FromMilliseconds(250);
    private readonly long _startedTimestamp;
    private readonly Process _process;
    private readonly Task<string> _standardErrorTask;
    private readonly SystemTextJsonFormatter _formatter;
    private readonly HeaderDelimitedMessageHandler _messageHandler;
    private readonly JsonRpc _rpc;

    private LanguageServerMeasurementSession(
        long startedTimestamp,
        Process process,
        Task<string> standardErrorTask,
        SystemTextJsonFormatter formatter,
        HeaderDelimitedMessageHandler messageHandler,
        JsonRpc rpc)
    {
        _startedTimestamp = startedTimestamp;
        _process = process;
        _standardErrorTask = standardErrorTask;
        _formatter = formatter;
        _messageHandler = messageHandler;
        _rpc = rpc;
    }

    /// <summary>
    /// Starts the published Native AOT launcher and connects its production LSP streams.
    /// </summary>
    /// <param name="serverPath">The absolute published csls launcher path.</param>
    /// <param name="workspacePath">The absolute measured workspace path.</param>
    /// <returns>The connected measurement session.</returns>
    internal static LanguageServerMeasurementSession Start(
        string serverPath,
        string workspacePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = serverPath,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workspacePath
        };
        startInfo.ArgumentList.Add("lsp");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        long startedTimestamp = Stopwatch.GetTimestamp();
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The published csls process did not start.");
        try
        {
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            var formatter = new SystemTextJsonFormatter
            {
                JsonSerializerOptions = LspRpcJson.CreateSerializerOptions()
            };
            var messageHandler = new HeaderDelimitedMessageHandler(
                process.StandardInput.BaseStream,
                process.StandardOutput.BaseStream,
                formatter);
            var rpc = new JsonRpc(messageHandler)
            {
                CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
                DisplayName = "csls-end-to-end-performance"
            };
            rpc.StartListening();
            return new LanguageServerMeasurementSession(
                startedTimestamp,
                process,
                standardErrorTask,
                formatter,
                messageHandler,
                rpc);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Initializes, loads, samples, and cleanly shuts down one real workspace session.
    /// </summary>
    /// <param name="iteration">The one-based process iteration number.</param>
    /// <param name="workspacePath">The absolute measured workspace path.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The complete end-to-end performance measurement.</returns>
    internal async Task<PerformanceMeasurement> MeasureAsync(
        int iteration,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        CSharpDebugInfo uninitialized = await RequestDebugInfoAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
            uninitialized.Workspace.Phase,
            "Uninitialized",
            StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"The new language server reported phase {uninitialized.Workspace.Phase}.");
        }

        TimeSpan startupDuration = Stopwatch.GetElapsedTime(_startedTimestamp);
        long workspaceStartedTimestamp = Stopwatch.GetTimestamp();
        using var capabilities = JsonDocument.Parse("{}");
        JsonElement initializationResult = await _rpc
            .InvokeWithParameterObjectAsync<JsonElement>(
                "initialize",
                new InitializeParams
                {
                    ProcessId = Environment.ProcessId,
                    ClientInfo = new ClientInfo { Name = "Csls.EndToEndPerformance" },
                    RootUri = DocumentUri.FromFileSystemPath(workspacePath),
                    WorkspaceFolders =
                    [
                        new WorkspaceFolder
                        {
                            Uri = DocumentUri.FromFileSystemPath(workspacePath),
                            Name = Path.GetFileName(workspacePath)
                        }
                    ],
                    Capabilities = capabilities.RootElement
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (initializationResult.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The language server returned an invalid initialize result.");
        }

        await _rpc.NotifyWithParameterObjectAsync(
            "initialized",
            new InitializedParams()).ConfigureAwait(false);
        CSharpDebugInfo debugInfo = await WaitUntilReadyAsync(cancellationToken)
            .ConfigureAwait(false);
        TimeSpan workspaceDuration = Stopwatch.GetElapsedTime(workspaceStartedTimestamp);
        TimeSpan readyDuration = Stopwatch.GetElapsedTime(_startedTimestamp);
        await Task.Delay(s_resourceSettleTime, cancellationToken).ConfigureAwait(false);
        ProcessTreeSnapshot processTree = await ProcessTreeReader.CaptureAsync(
            _process.Id,
            cancellationToken).ConfigureAwait(false);
        int projectCount = debugInfo.Workspace.Folders.Sum(static folder => folder.ProjectCount);
        int documentCount = debugInfo.Workspace.Folders.Sum(static folder => folder.DocumentCount);
        if (projectCount == 0 || documentCount == 0)
        {
            throw new InvalidDataException(
                "The measured workspace became ready without Roslyn projects and source documents.");
        }

        await ShutdownAsync(cancellationToken).ConfigureAwait(false);
        return new PerformanceMeasurement
        {
            Iteration = iteration,
            CacheState = iteration == 1 ? "cold" : "warm",
            StartupMilliseconds = startupDuration.TotalMilliseconds,
            WorkspaceLoadMilliseconds = workspaceDuration.TotalMilliseconds,
            ReadyMilliseconds = readyDuration.TotalMilliseconds,
            ProjectCount = projectCount,
            DocumentCount = documentCount,
            ProcessCount = processTree.ProcessCount,
            WorkingSetBytes = processTree.WorkingSetBytes,
            PrivateMemoryBytes = processTree.PrivateMemoryBytes
        };
    }

    /// <summary>
    /// Releases the RPC transport and terminates an unfinished process tree.
    /// </summary>
    /// <returns>A task that completes after process cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }

        _process.Dispose();
    }

    private async Task<CSharpDebugInfo> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(s_pollInterval);
        while (true)
        {
            CSharpDebugInfo result = await RequestDebugInfoAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(result.Workspace.Phase, "Ready", StringComparison.Ordinal))
            {
                return result;
            }

            if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new UnreachableException();
            }
        }
    }

    private Task<CSharpDebugInfo> RequestDebugInfoAsync(CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<CSharpDebugInfo>(
            "$/csharp/debugInfo",
            new InitializedParams(),
            cancellationToken);

    private async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        object? shutdownResult = await _rpc.InvokeWithParameterObjectAsync<object?>(
            "shutdown",
            new InitializedParams(),
            cancellationToken).ConfigureAwait(false);
        if (shutdownResult is not null)
        {
            throw new InvalidDataException("The LSP shutdown response must be null.");
        }

        await _rpc.NotifyWithParameterObjectAsync(
            "exit",
            new InitializedParams()).ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var standardError = new ValueTask<string>(_standardErrorTask);
        string diagnostics = await standardError.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The language server exited with code {_process.ExitCode}: {diagnostics}");
        }

        if (diagnostics.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The language server reported an unhandled exception: {diagnostics}");
        }
    }
}

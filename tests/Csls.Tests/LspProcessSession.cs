using Csls.Protocol;
using Csls.Rpc;
using Csls.Support;
using Csls.Workspaces;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Drives a real language-server process over its production standard streams.
/// </summary>
internal sealed partial class LspProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly WindowsProcessTreeLifetime _processTree;
    private readonly Task<string> _standardErrorTask;
    private readonly SystemTextJsonFormatter _formatter;
    private readonly HeaderDelimitedMessageHandler _messageHandler;
    private readonly JsonRpc _rpc;
    private int _initializationCompleted;

    private LspProcessSession(
        Process process,
        WindowsProcessTreeLifetime processTree,
        Task<string> standardErrorTask,
        SystemTextJsonFormatter formatter,
        HeaderDelimitedMessageHandler messageHandler,
        JsonRpc rpc)
    {
        _process = process;
        _processTree = processTree;
        _standardErrorTask = standardErrorTask;
        _formatter = formatter;
        _messageHandler = messageHandler;
        _rpc = rpc;
    }

    /// <summary>
    /// Gets the operating-system process identifier of the real language-server process.
    /// </summary>
    internal int ProcessId => _process.Id;

    /// <summary>
    /// Starts a real server process and connects a StreamJsonRpc LSP client to it.
    /// </summary>
    /// <param name="displayName">The diagnostic name for the JSON-RPC connection.</param>
    /// <param name="fileName">The server executable path.</param>
    /// <param name="arguments">The server command-line arguments.</param>
    /// <param name="workingDirectory">The isolated server working directory.</param>
    /// <param name="client">The optional bidirectional LSP client target.</param>
    /// <param name="environmentVariables">The optional child-process environment overrides.</param>
    /// <param name="diagnosticOutput">Receives server stderr as it arrives and remains owned by the caller.</param>
    /// <returns>A connected process session.</returns>
    internal static Task<LspProcessSession> StartAsync(
        string displayName,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        LspTestClient? client = null,
        IReadOnlyDictionary<string, string>? environmentVariables = null,
        TextWriter? diagnosticOutput = null)
    {
        return StartCoreAsync(
            displayName,
            fileName,
            arguments,
            workingDirectory,
            client,
            environmentVariables,
            diagnosticOutput);
    }

    private static async Task<LspProcessSession> StartCoreAsync(
        string displayName,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        LspTestClient? client,
        IReadOnlyDictionary<string, string>? environmentVariables,
        TextWriter? diagnosticOutput)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach ((string name, string value) in environmentVariables)
            {
                startInfo.Environment[name] = value;
            }
        }

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The {displayName} process did not start.");
        var processTree = WindowsProcessTreeLifetime.Attach(process);

        try
        {
            Task<string> standardErrorTask = diagnosticOutput is null
                ? process.StandardError.ReadToEndAsync()
                : ProcessOutputCapture.ReadAsync(process.StandardError.BaseStream,
                    process.StandardError.CurrentEncoding, diagnosticOutput, CancellationToken.None);
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
                DisplayName = displayName
            };
            if (client is not null)
            {
                Func<JsonElement, Task> logHandler = client.PublishLogMessageAsync;
                rpc.AddLocalRpcMethod(logHandler.Method,
                    logHandler.Target ?? throw new InvalidOperationException("The log handler has no client target."),
                    new JsonRpcMethodAttribute("window/logMessage")
                    {
                        UseSingleObjectParameterDeserialization = true
                    });
                Func<ConfigurationParams, CancellationToken, Task<JsonElement?[]>> handler =
                    client.GetConfigurationAsync;
                var attribute = new JsonRpcMethodAttribute("workspace/configuration")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    handler.Method,
                    handler.Target ?? throw new InvalidOperationException(
                        "The configuration handler has no client target."),
                    attribute);

                Func<RegistrationParams, CancellationToken, Task>
                    capabilityRegistrationHandler = client.RegisterCapabilityAsync;
                var capabilityRegistrationAttribute = new JsonRpcMethodAttribute(
                    "client/registerCapability")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    capabilityRegistrationHandler.Method,
                    capabilityRegistrationHandler.Target ??
                        throw new InvalidOperationException(
                            "The capability registration handler has no client target."),
                    capabilityRegistrationAttribute);

                Func<CancellationToken, Task> diagnosticRefreshHandler =
                    client.RefreshDiagnosticsAsync;
                rpc.AddLocalRpcMethod(
                    "workspace/diagnostic/refresh",
                    diagnosticRefreshHandler);

                Func<CancellationToken, Task> inlayHintRefreshHandler =
                    client.RefreshInlayHintsAsync;
                rpc.AddLocalRpcMethod(
                    "workspace/inlayHint/refresh",
                    inlayHintRefreshHandler);

                Func<CancellationToken, Task> codeLensRefreshHandler =
                    client.RefreshCodeLensesAsync;
                rpc.AddLocalRpcMethod(
                    "workspace/codeLens/refresh",
                    codeLensRefreshHandler);

                Func<WorkDoneProgressCreateParams, CancellationToken, Task>
                    workDoneProgressCreateHandler = client.CreateWorkDoneProgressAsync;
                var workDoneProgressCreateAttribute = new JsonRpcMethodAttribute(
                    "window/workDoneProgress/create")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    workDoneProgressCreateHandler.Method,
                    workDoneProgressCreateHandler.Target ??
                        throw new InvalidOperationException(
                            "The work-done progress creation handler has no client target."),
                    workDoneProgressCreateAttribute);

                Func<JsonElement, Task> progressHandler = client.PublishProgressAsync;
                var progressAttribute = new JsonRpcMethodAttribute("$/progress")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    progressHandler.Method,
                    progressHandler.Target ?? throw new InvalidOperationException(
                        "The progress handler has no client target."),
                    progressAttribute);

                Func<PublishDiagnosticsParams, Task> diagnosticHandler =
                    client.PublishDiagnosticsAsync;
                var diagnosticAttribute = new JsonRpcMethodAttribute(
                    "textDocument/publishDiagnostics")
                {
                    UseSingleObjectParameterDeserialization = true
                };
                rpc.AddLocalRpcMethod(
                    diagnosticHandler.Method,
                    diagnosticHandler.Target ?? throw new InvalidOperationException(
                        "The diagnostic handler has no client target."),
                    diagnosticAttribute);
            }

            rpc.StartListening();
            return new LspProcessSession(
                process,
                processTree,
                standardErrorTask,
                formatter,
                messageHandler,
                rpc);
        }
        catch
        {
            await processTree.DisposeAsync().ConfigureAwait(false);
            DisposeFailedStart(process);

            throw;
        }
    }

    /// <summary>
    /// Initializes the server against a real workspace and returns its raw result.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace directory.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeAsync(
        string workspacePath,
        CancellationToken cancellationToken)
    {
        using var capabilities = JsonDocument.Parse(
            """
            {
              "textDocument": {
                "diagnostic": {}
              }
            }
            """);
        return await InitializeAsync(
            workspacePath,
            capabilities.RootElement,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the server with an explicit LSP client process identifier.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace directory.</param>
    /// <param name="clientProcessId">The real client process identifier.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeWithProcessIdAsync(
        string workspacePath,
        int clientProcessId,
        CancellationToken cancellationToken)
    {
        using var capabilities = JsonDocument.Parse(
            """
            {
              "textDocument": {
                "diagnostic": {}
              }
            }
            """);
        return await InitializeAsync(
            [workspacePath],
            capabilities.RootElement,
            initializationOptions: null,
            clientProcessId,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the server with explicit client capabilities and returns its raw result.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace directory.</param>
    /// <param name="capabilities">The exact client capability object.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeAsync(
        string workspacePath,
        JsonElement capabilities,
        CancellationToken cancellationToken)
    {
        return await InitializeAsync(
            [workspacePath],
            capabilities,
            initializationOptions: null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the server with explicit capabilities and editor identity.
    /// </summary>
    /// <param name="workspacePath">The absolute workspace directory.</param>
    /// <param name="capabilities">The exact client capability object.</param>
    /// <param name="clientName">The editor name reported through LSP initialization.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeAsync(
        string workspacePath,
        JsonElement capabilities,
        string clientName,
        CancellationToken cancellationToken)
    {
        return await InitializeAsync(
            [workspacePath],
            capabilities,
            initializationOptions: null,
            Environment.ProcessId,
            cancellationToken,
            clientName).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the server with explicit folders, capabilities, and initialization settings.
    /// </summary>
    /// <param name="workspacePaths">The ordered absolute workspace directories.</param>
    /// <param name="capabilities">The exact client capability object.</param>
    /// <param name="initializationOptions">The optional initialization configuration payload.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The server initialization result.</returns>
    internal async Task<JsonElement> InitializeAsync(
        IReadOnlyList<string> workspacePaths,
        JsonElement capabilities,
        JsonElement? initializationOptions,
        CancellationToken cancellationToken)
    {
        return await InitializeAsync(
            workspacePaths,
            capabilities,
            initializationOptions,
            Environment.ProcessId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> InitializeAsync(
        IReadOnlyList<string> workspacePaths,
        JsonElement capabilities,
        JsonElement? initializationOptions,
        int? clientProcessId,
        CancellationToken cancellationToken,
        string clientName = "Csls.ParityTests")
    {
        ArgumentNullException.ThrowIfNull(workspacePaths);
        if (workspacePaths.Count == 0)
        {
            throw new ArgumentException("At least one workspace path is required.", nameof(workspacePaths));
        }

        return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize",
            new InitializeParams
            {
                ProcessId = clientProcessId,
                ClientInfo = new ClientInfo { Name = clientName },
                RootUri = DocumentUri.FromFileSystemPath(workspacePaths[0]),
                WorkspaceFolders =
                [
                    .. workspacePaths.Select(path => new WorkspaceFolder
                    {
                        Uri = DocumentUri.FromFileSystemPath(path),
                        Name = Path.GetFileName(path)
                    })
                ],
                Capabilities = capabilities,
                InitializationOptions = initializationOptions
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the initialized notification once for this real process session.
    /// </summary>
    /// <returns>A task that completes after the notification is written.</returns>
    internal Task CompleteInitializationAsync()
    {
        return Interlocked.Exchange(ref _initializationCompleted, 1) == 0
            ? _rpc.NotifyWithParameterObjectAsync(
                "initialized",
                new InitializedParams())
            : Task.CompletedTask;
    }


    /// <summary>
    /// Performs the LSP shutdown handshake and verifies a successful process exit.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The captured server diagnostics.</returns>
    internal async Task<string> ShutdownAsync(CancellationToken cancellationToken)
    {
        await RequestShutdownAsync(cancellationToken).ConfigureAwait(false);
        return await ExitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends shutdown without exit so the terminating workspace state remains observable.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>A task that completes after the server acknowledges shutdown.</returns>
    internal async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        object? shutdownResult = await _rpc.InvokeWithParameterObjectAsync<object?>(
            "shutdown",
            new InitializedParams(),
            cancellationToken).ConfigureAwait(false);
        if (shutdownResult is not null)
        {
            throw new InvalidDataException("The LSP shutdown response must be null.");
        }
    }

    /// <summary>
    /// Sends exit and verifies that the real language-server process terminates successfully.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The captured server diagnostics.</returns>
    internal async Task<string> ExitAsync(CancellationToken cancellationToken)
    {
        await _rpc.NotifyWithParameterObjectAsync(
            "exit",
            new InitializedParams()).ConfigureAwait(false);
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await _processTree.TerminateDescendantsAsync().ConfigureAwait(false);
        ValueTask<string> standardError = new(_standardErrorTask);
        string diagnostics = await standardError.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The language server exited with code {_process.ExitCode}: {diagnostics}");
        }

        return diagnostics;
    }

    /// <summary>
    /// Waits for the real server process to exit without closing its protocol streams.
    /// </summary>
    /// <param name="timeout">The maximum interval allowed for process cleanup.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The captured server diagnostics.</returns>
    internal async Task<string> WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken)
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
        await _processTree.TerminateDescendantsAsync().ConfigureAwait(false);
        ValueTask<string> standardError = new(_standardErrorTask);
        string diagnostics = await standardError.ConfigureAwait(false);
        if (_process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"The language server exited with code {_process.ExitCode}: {diagnostics}");
        }

        return diagnostics;
    }

    /// <summary>
    /// Releases the RPC transport and terminates an unfinished child process tree.
    /// </summary>
    /// <returns>A task that completes after process cleanup.</returns>
    public async ValueTask DisposeAsync()
    {
        using (_process)
        {
            _rpc.Dispose();
            await _messageHandler.DisposeAsync().ConfigureAwait(false);
            _formatter.Dispose();
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }

            await _processTree.DisposeAsync().ConfigureAwait(false);
            ValueTask<string> standardError = new(_standardErrorTask);
            await standardError.ConfigureAwait(false);
        }
    }

    private static void DisposeFailedStart(Process process)
    {
        using (process)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}

using Csls.Protocol;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Drives a real language-server process over its production standard streams.
/// </summary>
internal sealed class LspProcessSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _standardErrorTask;
    private readonly SystemTextJsonFormatter _formatter;
    private readonly HeaderDelimitedMessageHandler _messageHandler;
    private readonly JsonRpc _rpc;
    private int _initializationCompleted;

    private LspProcessSession(
        Process process,
        Task<string> standardErrorTask,
        SystemTextJsonFormatter formatter,
        HeaderDelimitedMessageHandler messageHandler,
        JsonRpc rpc)
    {
        _process = process;
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
    /// <returns>A connected process session.</returns>
    internal static LspProcessSession Start(
        string displayName,
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory)
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

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The {displayName} process did not start.");
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = LspJson.CreateSerializerOptions()
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
        rpc.StartListening();
        return new LspProcessSession(
            process,
            standardErrorTask,
            formatter,
            messageHandler,
            rpc);
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
        using var capabilities = JsonDocument.Parse("{}");
        return await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize",
            new InitializeParams
            {
                ProcessId = Environment.ProcessId,
                ClientInfo = new ClientInfo { Name = "Csls.ParityTests" },
                RootUri = DocumentUri.FromFileSystemPath(workspacePath),
                Capabilities = capabilities.RootElement
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Completes initialization and opens a real document in the server workspace.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="documentText">The exact on-disk document text.</param>
    /// <returns>A task that completes after both notifications are written.</returns>
    internal async Task OpenDocumentAsync(string documentPath, string documentText)
    {
        if (Interlocked.Exchange(ref _initializationCompleted, 1) == 0)
        {
            await _rpc.NotifyWithParameterObjectAsync(
                "initialized",
                new InitializedParams()).ConfigureAwait(false);
        }

        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = documentText
                }
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Requests hover information at an exact UTF-16 document position.
    /// </summary>
    /// <param name="documentPath">The absolute document path.</param>
    /// <param name="position">The requested document position.</param>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The raw optional hover result.</returns>
    internal Task<JsonElement?> RequestHoverAsync(
        string documentPath,
        Position position,
        CancellationToken cancellationToken) =>
        _rpc.InvokeWithParameterObjectAsync<JsonElement?>(
            "textDocument/hover",
            new TextDocumentPositionParams
            {
                TextDocument = new TextDocumentIdentifier
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath)
                },
                Position = position
            },
            cancellationToken);

    /// <summary>
    /// Performs the LSP shutdown handshake and verifies a successful process exit.
    /// </summary>
    /// <param name="cancellationToken">The test cancellation token.</param>
    /// <returns>The captured server diagnostics.</returns>
    internal async Task<string> ShutdownAsync(CancellationToken cancellationToken)
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
}

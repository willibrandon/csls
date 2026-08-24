using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Client;

/// <summary>
/// Owns one real transient csls language-server process and its LSP transport.
/// </summary>
public sealed class TransientLanguageServerSession : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task _standardErrorPump;
    private readonly SystemTextJsonFormatter _formatter;
    private readonly HeaderDelimitedMessageHandler _messageHandler;
    private readonly JsonRpc _rpc;
    private int _initializationCompleted;
    private int _disposeState;

    private TransientLanguageServerSession(
        Process process,
        Task standardErrorPump,
        SystemTextJsonFormatter formatter,
        HeaderDelimitedMessageHandler messageHandler,
        JsonRpc rpc)
    {
        _process = process;
        _standardErrorPump = standardErrorPump;
        _formatter = formatter;
        _messageHandler = messageHandler;
        _rpc = rpc;
    }

    /// <summary>
    /// Gets the operating-system identifier of the transient language-server process.
    /// </summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// Gets the completed transient language-server exit code.
    /// </summary>
    public int ExitCode => _process.ExitCode;

    /// <summary>
    /// Starts and initializes a real transient language-server worker for one workspace.
    /// </summary>
    /// <param name="workspacePath">The workspace directory, solution, project, or document path.</param>
    /// <param name="clientName">The LSP client name reported during initialization.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>The initialized transient session.</returns>
    public static async Task<TransientLanguageServerSession> StartAsync(
        string workspacePath,
        string clientName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientName);
        string fullWorkspacePath = Path.GetFullPath(workspacePath);
        if (!Directory.Exists(fullWorkspacePath) && !File.Exists(fullWorkspacePath))
        {
            throw new FileNotFoundException(
                "The transient csls workspace does not exist.",
                fullWorkspacePath);
        }

        string workerPath = LanguageServerWorkerLocator.Resolve();
        string workerDirectory = Path.GetDirectoryName(workerPath)
            ?? throw new InvalidOperationException(
                $"Language-server worker {workerPath} has no containing directory.");
        bool isManagedAssembly = string.Equals(
            Path.GetExtension(workerPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedAssembly ? ResolveDotNetHost() : workerPath,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = workerDirectory
        };
        if (isManagedAssembly)
        {
            startInfo.ArgumentList.Add(workerPath);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        startInfo.Environment["DOTNET_NOLOGO"] = "true";

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "The transient csls language-server worker did not start.");
        Task standardErrorPump = PumpStandardErrorAsync(process.StandardError);
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
            DisplayName = clientName
        };
        rpc.StartListening();
        var session = new TransientLanguageServerSession(
            process,
            standardErrorPump,
            formatter,
            messageHandler,
            rpc);
        try
        {
            await session.InitializeAsync(
                fullWorkspacePath,
                clientName,
                cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Waits for the transient language-server process to exit.
    /// </summary>
    /// <param name="cancellationToken">The wait cancellation token.</param>
    /// <returns>A task that completes when the process exits.</returns>
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    /// <summary>
    /// Gracefully shuts down the transient server and releases its process and RPC transport.
    /// </summary>
    /// <returns>A task that completes after all process resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        if (!_process.HasExited && Volatile.Read(ref _initializationCompleted) != 0)
        {
            using var shutdownSource = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                object? shutdownResult = await _rpc
                    .InvokeWithParameterObjectAsync<object?>(
                        "shutdown",
                        new InitializedParams(),
                        shutdownSource.Token).ConfigureAwait(false);
                if (shutdownResult is not null)
                {
                    throw new InvalidDataException(
                        "The transient LSP shutdown response must be null.");
                }

                await _rpc.NotifyWithParameterObjectAsync(
                    "exit",
                    new InitializedParams()).ConfigureAwait(false);
                await _process.WaitForExitAsync(shutdownSource.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or InvalidDataException or
                    ObjectDisposedException or RemoteInvocationException)
            {
                await Console.Error.WriteLineAsync(
                    $"Transient csls shutdown failed: {exception.Message}")
                    .ConfigureAwait(false);
            }
        }

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        ValueTask standardErrorCompletion = new(_standardErrorPump);
        await standardErrorCompletion.ConfigureAwait(false);
        _process.Dispose();
    }

    private static string ResolveDotNetHost()
    {
        string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        return string.IsNullOrWhiteSpace(hostPath) ? "dotnet" : hostPath;
    }

    private static async Task PumpStandardErrorAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            await Console.Error.WriteLineAsync(line).ConfigureAwait(false);
        }
    }

    private async Task InitializeAsync(
        string workspacePath,
        string clientName,
        CancellationToken cancellationToken)
    {
        using var capabilities = JsonDocument.Parse("{}");
        await _rpc.InvokeWithParameterObjectAsync<JsonElement>(
            "initialize",
            new InitializeParams
            {
                ProcessId = Environment.ProcessId,
                ClientInfo = new ClientInfo { Name = clientName },
                RootUri = DocumentUri.FromFileSystemPath(workspacePath),
                Capabilities = capabilities.RootElement
            },
            cancellationToken).ConfigureAwait(false);
        await _rpc.NotifyWithParameterObjectAsync(
            "initialized",
            new InitializedParams()).ConfigureAwait(false);
        Volatile.Write(ref _initializationCompleted, 1);
    }
}

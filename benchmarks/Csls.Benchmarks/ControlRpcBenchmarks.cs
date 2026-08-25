using BenchmarkDotNet.Attributes;
using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;

namespace Csls.Benchmarks;

/// <summary>
/// Measures production control requests through a real Unix-domain socket and workspace.
/// </summary>
[BenchmarkCategory("Control", "Rpc")]
[MemoryDiagnoser]
public class ControlRpcBenchmarks : IAsyncDisposable
{
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);
    private Process _process = null!;
    private Task<string> _standardErrorTask = null!;
    private SystemTextJsonFormatter _formatter = null!;
    private HeaderDelimitedMessageHandler _messageHandler = null!;
    private JsonRpc _rpc = null!;
    private ControlRpcClient _controlClient = null!;
    private ControlDashboardRequest _dashboardRequest = null!;
    private ControlWorkspaceSymbolRequest _workspaceSymbolRequest = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Starts a real worker, loads its project, and connects the production control client.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-control-rpc-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "ControlTarget.cs"),
            DocumentText).ConfigureAwait(false);

        string repositoryRoot = FindRepositoryRoot();
        string workerPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "release",
            "csls-worker.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = _fixturePath
        };
        startInfo.ArgumentList.Add(workerPath);
        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The control RPC benchmark worker did not start.");
        _standardErrorTask = _process.StandardError.ReadToEndAsync();
        _formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = LspRpcJson.CreateSerializerOptions()
        };
        _messageHandler = new HeaderDelimitedMessageHandler(
            _process.StandardInput.BaseStream,
            _process.StandardOutput.BaseStream,
            _formatter);
        _rpc = new JsonRpc(_messageHandler)
        {
            CancelLocallyInvokedMethodsWhenConnectionIsClosed = true,
            DisplayName = "csls-control-rpc-benchmark"
        };
        _rpc.StartListening();
        using var capabilities = JsonDocument.Parse("{}");
        await _rpc.InvokeWithParameterObjectAsync<InitializeResult>(
            "initialize",
            new InitializeParams
            {
                ProcessId = Environment.ProcessId,
                ClientInfo = new ClientInfo { Name = "Csls.Benchmarks" },
                RootUri = DocumentUri.FromFileSystemPath(_fixturePath),
                Capabilities = capabilities.RootElement
            },
            CancellationToken.None).ConfigureAwait(false);
        await _rpc.NotifyWithParameterObjectAsync(
            "initialized",
            new InitializedParams()).ConfigureAwait(false);
        await WaitUntilReadyAsync().ConfigureAwait(false);

        _controlClient = new ControlRpcClient(ControlEndpoint.GetSocketPath(_process.Id));
        await WaitForControlSessionAsync().ConfigureAwait(false);
        _dashboardRequest = new ControlDashboardRequest { IncludeDiagnostics = false };
        _workspaceSymbolRequest = new ControlWorkspaceSymbolRequest { Query = "ControlTarget" };
        ControlDashboardSnapshot dashboard = await GetDashboardSnapshotAsync()
            .ConfigureAwait(false);
        IReadOnlyList<WorkspaceSymbol> symbols = await SearchWorkspaceSymbolsAsync()
            .ConfigureAwait(false);
        if (dashboard.Projects.Count == 0 ||
            !symbols.Any(static symbol => symbol.Name == "ControlTarget"))
        {
            throw new InvalidOperationException(
                "The control RPC benchmark could not validate its real workspace.");
        }
    }

    /// <summary>
    /// Disposes the socket client, worker, transport, and isolated project fixture.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Measures a minimal versioned request through the connected control socket.
    /// </summary>
    [Benchmark]
    public Task<ControlSessionInfo> GetSessionAsync() =>
        _controlClient.GetSessionAsync(CancellationToken.None);

    /// <summary>
    /// Measures bounded dashboard projection and serialization through the control socket.
    /// </summary>
    [Benchmark]
    public Task<ControlDashboardSnapshot> GetDashboardSnapshotAsync() =>
        _controlClient.GetDashboardSnapshotAsync(
            _dashboardRequest,
            CancellationToken.None);

    /// <summary>
    /// Measures indexed workspace search through the production control protocol.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<WorkspaceSymbol>> SearchWorkspaceSymbolsAsync() =>
        _controlClient.GetWorkspaceSymbolsAsync(
            _workspaceSymbolRequest,
            CancellationToken.None);

    /// <summary>
    /// Shuts down the worker and releases the control and language-server transports.
    /// </summary>
    /// <returns>A value task that completes after all resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _controlClient.DisposeAsync().ConfigureAwait(false);
        await _rpc.InvokeWithParameterObjectAsync<object?>(
            "shutdown",
            new InitializedParams(),
            CancellationToken.None).ConfigureAwait(false);
        await _rpc.NotifyWithParameterObjectAsync(
            "exit",
            new InitializedParams()).ConfigureAwait(false);
        await _process.WaitForExitAsync().ConfigureAwait(false);
        ValueTask<string> standardErrorTask = new(_standardErrorTask);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        if (_process.ExitCode != 0 ||
            standardError.Contains("Unhandled exception", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The control RPC benchmark worker failed with exit code {_process.ExitCode}: {standardError}");
        }

        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        _process.Dispose();
        Directory.Delete(_fixturePath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task WaitUntilReadyAsync()
    {
        using var timeout = new CancellationTokenSource(s_readyTimeout);
        while (true)
        {
            CSharpDebugInfo debugInfo = await _rpc
                .InvokeWithParameterObjectAsync<CSharpDebugInfo>(
                    "$/csharp/debugInfo",
                    new InitializedParams(),
                    timeout.Token)
                .ConfigureAwait(false);
            if (string.Equals(debugInfo.Workspace.Phase, "Ready", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    private async Task WaitForControlSessionAsync()
    {
        using var timeout = new CancellationTokenSource(s_readyTimeout);
        while (true)
        {
            try
            {
                ControlSessionInfo session = await _controlClient
                    .GetSessionAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (session.ProcessId == _process.Id)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException)
            {
                await Console.Error.WriteLineAsync(
                    $"Waiting for the control session: {exception.Message}")
                    .ConfigureAwait(false);
            }

            await Task.Delay(50, timeout.Token).ConfigureAwait(false);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "Csls.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The csls repository root was not found.");
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public sealed class ControlTarget;
        """;
}

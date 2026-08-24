using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Benchmarks;

/// <summary>
/// Measures cached cross-document diagnostics through a real analyzer-enabled workspace.
/// </summary>
[BenchmarkCategory("Diagnostics")]
[MemoryDiagnoser]
public class AnalyzerDiagnosticsBenchmarks : IAsyncDisposable
{
    private Process _process = null!;
    private Task<string> _standardErrorTask = null!;
    private SystemTextJsonFormatter _formatter = null!;
    private HeaderDelimitedMessageHandler _messageHandler = null!;
    private JsonRpc _rpc = null!;
    private DocumentDiagnosticParams _parameters = null!;
    private WorkspaceDiagnosticParams _workspaceParameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real project and primes its project-wide analyzer result from another document.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-diagnostics-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string firstDocumentPath = Path.Join(_fixturePath, "FirstDocument.cs");
        string secondDocumentPath = Path.Join(_fixturePath, "SecondDocument.cs");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            firstDocumentPath,
            FirstDocumentText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            secondDocumentPath,
            SecondDocumentText).ConfigureAwait(false);

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
            ?? throw new InvalidOperationException("The diagnostics benchmark worker did not start.");
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
            DisplayName = "csls-diagnostics-benchmark"
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
        await OpenDocumentAsync(firstDocumentPath, FirstDocumentText).ConfigureAwait(false);
        await OpenDocumentAsync(secondDocumentPath, SecondDocumentText).ConfigureAwait(false);
        DocumentDiagnosticReport warmReport = await _rpc
            .InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
                "textDocument/diagnostic",
                CreateParameters(firstDocumentPath),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (warmReport.Items is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                "The diagnostics benchmark did not execute the real SDK analyzers.");
        }

        _parameters = CreateParameters(secondDocumentPath);
        _workspaceParameters = new WorkspaceDiagnosticParams
        {
            Identifier = "csls",
            PreviousResultIds = []
        };
    }

    /// <summary>
    /// Disposes the real workspace and removes the isolated analyzer project.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Measures a document pull that reuses another document's cached project analysis.
    /// </summary>
    [Benchmark]
    public Task<DocumentDiagnosticReport> PullCachedCrossDocumentDiagnosticsAsync() =>
        _rpc.InvokeWithParameterObjectAsync<DocumentDiagnosticReport>(
            "textDocument/diagnostic",
            _parameters,
            CancellationToken.None);

    /// <summary>
    /// Measures a complete workspace pull that reuses cached real project analysis.
    /// </summary>
    [Benchmark]
    public Task<WorkspaceDiagnosticReport> PullCachedWorkspaceDiagnosticsAsync() =>
        _rpc.InvokeWithParameterObjectAsync<WorkspaceDiagnosticReport>(
            "workspace/diagnostic",
            _workspaceParameters,
            CancellationToken.None);

    /// <summary>
    /// Shuts down the worker and releases the benchmark transport and fixture.
    /// </summary>
    /// <returns>A value task that completes after all resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

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
                $"The diagnostics benchmark worker failed with exit code {_process.ExitCode}: {standardError}");
        }

        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        _process.Dispose();
        Directory.Delete(_fixturePath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private Task OpenDocumentAsync(string documentPath, string text) =>
        _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = text
                }
            });

    private static DocumentDiagnosticParams CreateParameters(string documentPath) => new()
    {
        TextDocument = new TextDocumentIdentifier
        {
            Uri = DocumentUri.FromFileSystemPath(documentPath)
        },
        Identifier = "csls"
    };

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
            <EnableNETAnalyzers>true</EnableNETAnalyzers>
            <AnalysisLevel>latest</AnalysisLevel>
            <AnalysisMode>AllEnabledByDefault</AnalysisMode>
          </PropertyGroup>
        </Project>
        """;

    private const string FirstDocumentText = """
        namespace DiagnosticsBenchmark;

        public sealed class FirstDocument
        {
            public int GetValue() => 1;
        }
        """;

    private const string SecondDocumentText = """
        namespace DiagnosticsBenchmark;

        public sealed class SecondDocument
        {
            public int GetValue() => 2;
        }
        """;
}

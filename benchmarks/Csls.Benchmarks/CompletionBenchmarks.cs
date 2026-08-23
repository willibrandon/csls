using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Benchmarks;

/// <summary>
/// Measures real Roslyn completion and lazy documentation resolution.
/// </summary>
[BenchmarkCategory("Completion")]
[MemoryDiagnoser]
public class CompletionBenchmarks : IAsyncDisposable
{
    private Process _process = null!;
    private Task<string> _standardErrorTask = null!;
    private SystemTextJsonFormatter _formatter = null!;
    private HeaderDelimitedMessageHandler _messageHandler = null!;
    private JsonRpc _rpc = null!;
    private CompletionParams _parameters = null!;
    private CompletionItem _resolveItem = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads one real MSBuild project and primes a resolvable completion item.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-completion-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Program.cs");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(documentPath, DocumentText).ConfigureAwait(false);

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
            ?? throw new InvalidOperationException("The completion benchmark worker did not start.");
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
            DisplayName = "csls-completion-benchmark"
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
        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = DocumentText
                }
            }).ConfigureAwait(false);
        _parameters = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Position = new Position(6, 19),
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.Invoked
            }
        };
        CompletionList completion = await _rpc
            .InvokeWithParameterObjectAsync<CompletionList>(
                "textDocument/completion",
                _parameters,
                CancellationToken.None)
            .ConfigureAwait(false);
        _resolveItem = completion.Items.Single(
            static item => item.Label == "WriteLine");
    }

    /// <summary>
    /// Disposes the real workspace and removes the isolated project fixture.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Measures bounded Roslyn member completion with exact commit edits.
    /// </summary>
    [Benchmark]
    public Task<CompletionList> CompleteMemberAsync() =>
        _rpc.InvokeWithParameterObjectAsync<CompletionList>(
            "textDocument/completion",
            _parameters,
            CancellationToken.None);

    /// <summary>
    /// Measures deterministic recomputation and Roslyn documentation resolution.
    /// </summary>
    [Benchmark]
    public Task<CompletionItem> ResolveMemberAsync() =>
        _rpc.InvokeWithParameterObjectAsync<CompletionItem>(
            "completionItem/resolve",
            _resolveItem,
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
                $"The completion benchmark worker failed with exit code {_process.ExitCode}: {standardError}");
        }

        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        _process.Dispose();
        Directory.Delete(_fixturePath, recursive: true);
        GC.SuppressFinalize(this);
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

        public static class Program
        {
            public static void Main()
            {
                Console.Wri
            }
        }
        """;
}

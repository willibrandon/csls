using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Csls.Benchmarks;

/// <summary>
/// Measures workspace routing, UTF-16 position conversion, and indexed language operations.
/// </summary>
[BenchmarkCategory("Workspace", "LanguageOperations")]
[MemoryDiagnoser]
public class WorkspaceLanguageOperationBenchmarks : IAsyncDisposable
{
    private const int LongLineVariableCount = 1_000;
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromMinutes(2);
    private Process _process = null!;
    private Task<string> _standardErrorTask = null!;
    private SystemTextJsonFormatter _formatter = null!;
    private HeaderDelimitedMessageHandler _messageHandler = null!;
    private JsonRpc _rpc = null!;
    private TextDocumentPositionParams _hoverParameters = null!;
    private SemanticTokensParams _longLineSemanticTokensParameters = null!;
    private SemanticTokensParams _semanticTokensParameters = null!;
    private WorkspaceSymbolParams _workspaceSymbolParameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads two real workspace roots and validates every measured language operation.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-language-benchmark-{Guid.NewGuid():N}");
        string firstWorkspacePath = Path.Join(_fixturePath, "First");
        string secondWorkspacePath = Path.Join(_fixturePath, "Second");
        Directory.CreateDirectory(firstWorkspacePath);
        Directory.CreateDirectory(secondWorkspacePath);
        await File.WriteAllTextAsync(
            Path.Join(firstWorkspacePath, "First.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(firstWorkspacePath, "FirstType.cs"),
            FirstDocumentText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(secondWorkspacePath, "Second.csproj"),
            ProjectText).ConfigureAwait(false);
        string documentPath = Path.Join(secondWorkspacePath, "UnicodeRoutingTarget.cs");
        await File.WriteAllTextAsync(documentPath, SecondDocumentText).ConfigureAwait(false);
        string longLineDocumentPath = Path.Join(secondWorkspacePath, "LongLine.cs");
        string longLineDocumentText = CreateLongLineDocument();
        await File.WriteAllTextAsync(longLineDocumentPath, longLineDocumentText)
            .ConfigureAwait(false);

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
            ?? throw new InvalidOperationException(
                "The workspace language benchmark worker did not start.");
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
            DisplayName = "csls-workspace-language-benchmark"
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
                WorkspaceFolders =
                [
                    new WorkspaceFolder
                    {
                        Uri = DocumentUri.FromFileSystemPath(firstWorkspacePath),
                        Name = "First"
                    },
                    new WorkspaceFolder
                    {
                        Uri = DocumentUri.FromFileSystemPath(secondWorkspacePath),
                        Name = "Second"
                    }
                ],
                Capabilities = capabilities.RootElement
            },
            CancellationToken.None).ConfigureAwait(false);
        await _rpc.NotifyWithParameterObjectAsync(
            "initialized",
            new InitializedParams()).ConfigureAwait(false);
        await WaitUntilReadyAsync().ConfigureAwait(false);
        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = SecondDocumentText
                }
            }).ConfigureAwait(false);
        await _rpc.NotifyWithParameterObjectAsync(
            "textDocument/didOpen",
            new DidOpenTextDocumentParams
            {
                TextDocument = new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(longLineDocumentPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = longLineDocumentText
                }
            }).ConfigureAwait(false);

        string targetLine = SecondDocumentText
            .Split('\n', StringSplitOptions.None)
            .Single(static line => line.Contains("return message.Length", StringComparison.Ordinal));
        int targetLineNumber = Array.FindIndex(
            SecondDocumentText.Split('\n', StringSplitOptions.None),
            static line => line.Contains("return message.Length", StringComparison.Ordinal));
        int targetCharacter = targetLine.LastIndexOf("message", StringComparison.Ordinal);
        var documentIdentifier = new TextDocumentIdentifier
        {
            Uri = DocumentUri.FromFileSystemPath(documentPath)
        };
        _hoverParameters = new TextDocumentPositionParams
        {
            TextDocument = documentIdentifier,
            Position = new Position(targetLineNumber, targetCharacter)
        };
        _semanticTokensParameters = new SemanticTokensParams
        {
            TextDocument = documentIdentifier
        };
        _longLineSemanticTokensParameters = new SemanticTokensParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(longLineDocumentPath)
            }
        };
        _workspaceSymbolParameters = new WorkspaceSymbolParams
        {
            Query = "UnicodeRoutingTarget"
        };

        Hover? hover = await HoverThroughSecondWorkspaceAsync().ConfigureAwait(false);
        SemanticTokens tokens = await GetSemanticTokensAsync().ConfigureAwait(false);
        SemanticTokens longLineTokens = await GetLongLineSemanticTokensAsync()
            .ConfigureAwait(false);
        IReadOnlyList<WorkspaceSymbol> symbols = await SearchWorkspaceSymbolsAsync()
            .ConfigureAwait(false);
        if (hover is null ||
            tokens.Data.Count == 0 ||
            longLineTokens.Data.Count < LongLineVariableCount * 5 ||
            !symbols.Any(static symbol => symbol.Name == "UnicodeRoutingTarget"))
        {
            throw new InvalidOperationException(
                "The workspace language benchmark could not validate its real operations.");
        }
    }

    /// <summary>
    /// Disposes the real workspace and removes both isolated project roots.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Measures routed hover lookup at a UTF-16 position following a surrogate pair.
    /// </summary>
    [Benchmark]
    public Task<Hover?> HoverThroughSecondWorkspaceAsync() =>
        _rpc.InvokeWithParameterObjectAsync<Hover?>(
            "textDocument/hover",
            _hoverParameters,
            CancellationToken.None);

    /// <summary>
    /// Measures complete Roslyn semantic token classification for the routed document.
    /// </summary>
    [Benchmark]
    public Task<SemanticTokens> GetSemanticTokensAsync() =>
        _rpc.InvokeWithParameterObjectAsync<SemanticTokens>(
            "textDocument/semanticTokens/full",
            _semanticTokensParameters,
            CancellationToken.None);

    /// <summary>
    /// Measures semantic-token normalization for many classifications on one source line.
    /// </summary>
    [Benchmark]
    public Task<SemanticTokens> GetLongLineSemanticTokensAsync() =>
        _rpc.InvokeWithParameterObjectAsync<SemanticTokens>(
            "textDocument/semanticTokens/full",
            _longLineSemanticTokensParameters,
            CancellationToken.None);

    /// <summary>
    /// Measures indexed symbol search across both loaded workspace roots.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<WorkspaceSymbol>> SearchWorkspaceSymbolsAsync() =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<WorkspaceSymbol>>(
            "workspace/symbol",
            _workspaceSymbolParameters,
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
                $"The workspace language benchmark worker failed with exit code {_process.ExitCode}: {standardError}");
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

    private static string CreateLongLineDocument()
    {
        var builder = new StringBuilder(LongLineVariableCount * 24);
        builder.Append(
            "namespace Second; public static class LongLineTarget { " +
            "public static int Measure() { ");
        for (int index = 0; index < LongLineVariableCount; index++)
        {
            string indexText = index.ToString(CultureInfo.InvariantCulture);
            builder.Append("int value");
            builder.Append(indexText);
            builder.Append(" = ");
            builder.Append(indexText);
            builder.Append("; ");
        }

        builder.Append("return value");
        builder.Append((LongLineVariableCount - 1).ToString(CultureInfo.InvariantCulture));
        builder.Append("; } }");
        return builder.ToString();
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string FirstDocumentText = """
        namespace First;

        public sealed class FirstWorkspaceType;
        """;

    private const string SecondDocumentText = """
        namespace Second;

        public static class UnicodeRoutingTarget
        {
            public static int Measure()
            {
                string message = "😀"; return message.Length;
            }
        }
        """;
}

using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Rpc;
using StreamJsonRpc;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Csls.Benchmarks;

/// <summary>
/// Measures folding-range discovery through a real language-server worker.
/// </summary>
[BenchmarkCategory("Folding")]
[MemoryDiagnoser]
public class FoldingRangeBenchmarks : IAsyncDisposable
{
    private Process _process = null!;
    private Task<string> _standardErrorTask = null!;
    private SystemTextJsonFormatter _formatter = null!;
    private HeaderDelimitedMessageHandler _messageHandler = null!;
    private JsonRpc _rpc = null!;
    private FoldingRangeParams _parameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads one real MSBuild project containing representative foldable C# syntax.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-folding-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Program.cs");
        string documentText = CreateDocumentText();
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(documentPath, documentText).ConfigureAwait(false);

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
            ?? throw new InvalidOperationException("The folding benchmark worker did not start.");
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
            DisplayName = "csls-folding-benchmark"
        };
        _rpc.StartListening();
        using var capabilities = JsonDocument.Parse(
            """
            {
              "textDocument": {
                "foldingRange": {
                  "rangeLimit": 5000,
                  "foldingRangeKind": {
                    "valueSet": ["comment", "imports", "region"]
                  },
                  "foldingRange": {
                    "collapsedText": true
                  }
                }
              }
            }
            """);
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
                    Text = documentText
                }
            }).ConfigureAwait(false);
        _parameters = new FoldingRangeParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            }
        };
        IReadOnlyList<FoldingRange> ranges = await GetFoldingRangesAsync().ConfigureAwait(false);
        if (ranges.Count < 500)
        {
            throw new InvalidOperationException(
                $"The folding benchmark fixture produced only {ranges.Count} ranges.");
        }
    }

    /// <summary>
    /// Disposes the real workspace and removes the isolated project fixture.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Measures bounded folding discovery and protocol serialization.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<FoldingRange>> GetFoldingRangesAsync() =>
        _rpc.InvokeWithParameterObjectAsync<IReadOnlyList<FoldingRange>>(
            "textDocument/foldingRange",
            _parameters,
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
                $"The folding benchmark worker failed with exit code {_process.ExitCode}: {standardError}");
        }

        _rpc.Dispose();
        await _messageHandler.DisposeAsync().ConfigureAwait(false);
        _formatter.Dispose();
        _process.Dispose();
        Directory.Delete(_fixturePath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string CreateDocumentText()
    {
        var builder = new StringBuilder(
            "using System;\nusing System.Collections.Generic;\n\nnamespace Fixture\n{\n" +
            "    public static class Program\n    {\n        #region Generated\n");
        for (int index = 0; index < 256; index++)
        {
            builder.Append("        // Returns generated value ")
                .Append(index)
                .Append(".\n        // Exercises nested syntax.\n")
                .Append("        public static int Method")
                .Append(index)
                .Append("(int value)\n        {\n")
                .Append("            if (value > ")
                .Append(index)
                .Append(")\n            {\n                return value;\n            }\n\n")
                .Append("            return ")
                .Append(index)
                .Append(";\n        }\n\n");
        }

        return builder.Append("        #endregion\n    }\n}\n").ToString();
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
}

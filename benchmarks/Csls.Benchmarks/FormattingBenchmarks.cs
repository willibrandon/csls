using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using LspRange = Csls.Protocol.Range;

namespace Csls.Benchmarks;

/// <summary>
/// Measures complete and range-limited formatting for a current C# project snapshot.
/// </summary>
[BenchmarkCategory("Formatting")]
[MemoryDiagnoser]
public class FormattingBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private DocumentFormattingParams _documentParameters = null!;
    private DocumentRangeFormattingParams _rangeParameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real SDK project and verifies both formatting benchmark paths.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-formatting-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Program.cs");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(documentPath, DocumentText).ConfigureAwait(false);

        _workspaceManager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        await _workspaceManager.LoadAsync([_fixturePath], CancellationToken.None)
            .ConfigureAwait(false);
        var identifier = new TextDocumentIdentifier
        {
            Uri = DocumentUri.FromFileSystemPath(documentPath)
        };
        var options = new FormattingOptions
        {
            TabSize = 4,
            InsertSpaces = true
        };
        _documentParameters = new DocumentFormattingParams
        {
            TextDocument = identifier,
            Options = options
        };
        _rangeParameters = new DocumentRangeFormattingParams
        {
            TextDocument = identifier,
            Range = new LspRange(new Position(4, 0), new Position(5, 0)),
            Options = options
        };

        IReadOnlyList<TextEdit> documentEdits = await _workspaceManager
            .GetFormattingEditsAsync(_documentParameters, CancellationToken.None)
            .ConfigureAwait(false);
        IReadOnlyList<TextEdit> rangeEdits = await _workspaceManager
            .GetRangeFormattingEditsAsync(_rangeParameters, CancellationToken.None)
            .ConfigureAwait(false);
        if (documentEdits.Count == 0 || rangeEdits.Count == 0)
        {
            throw new InvalidOperationException(
                "The C# formatting benchmark fixture produced no edits.");
        }
    }

    /// <summary>
    /// Measures repeated complete formatting of an immutable C# document.
    /// </summary>
    [Benchmark(Baseline = true)]
    public Task<IReadOnlyList<TextEdit>> FormatDocumentAsync() =>
        _workspaceManager.GetFormattingEditsAsync(
            _documentParameters,
            CancellationToken.None);

    /// <summary>
    /// Measures repeated range formatting of an immutable C# document.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<TextEdit>> FormatRangeAsync() =>
        _workspaceManager.GetRangeFormattingEditsAsync(
            _rangeParameters,
            CancellationToken.None);

    /// <summary>
    /// Disposes the real workspace and removes the isolated project fixture.
    /// </summary>
    [GlobalCleanup]
    public async Task CleanupAsync() => await DisposeAsync().ConfigureAwait(false);

    /// <summary>
    /// Releases the Roslyn workspace and its temporary project files.
    /// </summary>
    /// <returns>A value task that completes after all resources are released.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _workspaceManager.DisposeAsync().ConfigureAwait(false);
        Directory.Delete(_fixturePath, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace FormattingBenchmark;

        public static class Calculator
        {
        public static int Add(int left,int right)=>left+right;
        public static int Subtract(int left,int right)=>left-right;
        }
        """;
}

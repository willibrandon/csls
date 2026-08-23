using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csls.Benchmarks;

/// <summary>
/// Measures complete-document formatting for a current Razor project snapshot.
/// </summary>
[BenchmarkCategory("Razor", "Formatting")]
[MemoryDiagnoser]
public class RazorFormattingBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private DocumentFormattingParams _parameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real SDK Web project and verifies its Razor formatting fixture.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-razor-formatting-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Component.razor");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "_Imports.razor"),
            string.Empty).ConfigureAwait(false);
        await File.WriteAllTextAsync(documentPath, RazorText).ConfigureAwait(false);

        _workspaceManager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
        await _workspaceManager.LoadAsync([_fixturePath], CancellationToken.None)
            .ConfigureAwait(false);
        await _workspaceManager.OpenDocumentAsync(
            new TextDocumentItem
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath),
                LanguageId = "razor",
                Version = 1,
                Text = RazorText
            },
            CancellationToken.None).ConfigureAwait(false);
        _parameters = new DocumentFormattingParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Options = new FormattingOptions
            {
                TabSize = 4,
                InsertSpaces = true,
                TrimTrailingWhitespace = true,
                InsertFinalNewline = true,
                TrimFinalNewlines = true
            }
        };
        IReadOnlyList<TextEdit> edits = await _workspaceManager.GetFormattingEditsAsync(
            _parameters,
            CancellationToken.None).ConfigureAwait(false);
        if (edits.Count == 0)
        {
            throw new InvalidOperationException(
                "The Razor formatting benchmark fixture produced no edits.");
        }
    }

    /// <summary>
    /// Measures repeated formatting of an immutable current Razor document.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<TextEdit>> FormatCurrentDocumentAsync() =>
        _workspaceManager.GetFormattingEditsAsync(
            _parameters,
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
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string RazorText = """
        <div>
        @if(true)
        {
        <span>@(1+2)</span>
        }
        </div>
        @code{
        private int count=0;
        }
        """;
}

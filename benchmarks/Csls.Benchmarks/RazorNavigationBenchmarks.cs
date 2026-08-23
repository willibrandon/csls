using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;

namespace Csls.Benchmarks;

/// <summary>
/// Measures project-aware navigation through cached SDK-generated Razor C#.
/// </summary>
[BenchmarkCategory("Razor")]
[MemoryDiagnoser]
public class RazorNavigationBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private TextDocumentPositionParams _parameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real SDK Web project and primes its generated-document index.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-razor-navigation-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Component.razor");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Known.cs"),
            KnownText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "_Imports.razor"),
            "@using Fixture").ConfigureAwait(false);
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
        _parameters = new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Position = new Position(0, 6)
        };
        IReadOnlyList<Location> locations = await _workspaceManager
            .GetDefinitionsAsync(_parameters, CancellationToken.None)
            .ConfigureAwait(false);
        if (locations.Count != 1)
        {
            throw new InvalidOperationException(
                "The Razor navigation benchmark did not resolve its fixture.");
        }
    }

    /// <summary>
    /// Measures repeated definition lookup after generated-document mapping has been indexed.
    /// </summary>
    [Benchmark]
    public Task<IReadOnlyList<Location>> CachedDefinitionAsync() =>
        _workspaceManager.GetDefinitionsAsync(_parameters, CancellationToken.None);

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

    private const string KnownText = """
        namespace Fixture;

        /// <summary>
        /// Supplies the value used by the Razor navigation benchmark.
        /// </summary>
        public static class Known
        {
            /// <summary>
            /// Gets the benchmark value.
            /// </summary>
            public static string Value => "benchmark";
        }
        """;

    private const string RazorText = "<p>@Known.Value</p>";
}

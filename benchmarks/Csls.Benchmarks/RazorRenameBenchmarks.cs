using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;

namespace Csls.Benchmarks;

/// <summary>
/// Measures cross-language rename from cached SDK-generated Razor C#.
/// </summary>
[BenchmarkCategory("Razor", "Rename")]
[MemoryDiagnoser]
public class RazorRenameBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private RenameParams _parameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real SDK Web project and verifies its cross-language rename edit.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-razor-rename-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Component.razor");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "SharedValues.cs"),
            SharedValuesText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "SharedConsumer.cs"),
            SharedConsumerText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "_Imports.razor"),
            "@using Fixture").ConfigureAwait(false);
        await File.WriteAllTextAsync(documentPath, RazorText).ConfigureAwait(false);

        _workspaceManager = BenchmarkWorkspaceManagerFactory.Create();
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
        _parameters = new RenameParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Position = new Position(0, 14),
            NewName = "SharedText"
        };
        WorkspaceEdit edit = await _workspaceManager.GetRenameEditAsync(
            _parameters,
            CancellationToken.None).ConfigureAwait(false);
        if (edit.DocumentChanges.Count != 3 ||
            edit.DocumentChanges
                .OfType<TextDocumentEdit>()
                .Sum(static document => document.Edits.Count) != 4)
        {
            throw new InvalidOperationException(
                "The Razor rename benchmark did not resolve its cross-language fixture.");
        }
    }

    /// <summary>
    /// Measures repeated cross-language rename previews from an immutable Razor snapshot.
    /// </summary>
    [Benchmark]
    public Task<WorkspaceEdit> CachedRenameAsync() =>
        _workspaceManager.GetRenameEditAsync(_parameters, CancellationToken.None);

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

    private const string SharedValuesText = """
        namespace Fixture;

        /// <summary>
        /// Supplies the value used by the Razor rename benchmark.
        /// </summary>
        public static class Known
        {
            /// <summary>
            /// Gets the benchmark value.
            /// </summary>
            public static string SharedValue => "benchmark";
        }
        """;

    private const string SharedConsumerText = """
        namespace Fixture;

        /// <summary>
        /// References the value used by the Razor rename benchmark.
        /// </summary>
        public static class SharedConsumer
        {
            /// <summary>
            /// Gets the shared benchmark value.
            /// </summary>
            public static string Current => Known.SharedValue;
        }
        """;

    private const string RazorText = """
        <p>@Known.SharedValue</p>
        <span>@Known.SharedValue</span>
        """;
}

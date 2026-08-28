using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;

namespace Csls.Benchmarks;

/// <summary>
/// Measures project-aware C# completion through cached SDK-generated Razor source.
/// </summary>
[BenchmarkCategory("Razor", "Completion")]
[MemoryDiagnoser]
public class RazorCompletionBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private CompletionParams _parameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real SDK Web project and primes Razor completion and import mapping.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-razor-completion-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Component.razor");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "_Imports.razor"),
            string.Empty).ConfigureAwait(false);
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
        _parameters = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Position = new Position(1, 21),
            Context = new CompletionContext
            {
                TriggerKind = CompletionTriggerKind.Invoked
            }
        };
        CompletionList completion = await _workspaceManager.GetCompletionsAsync(
            _parameters,
            supportsSnippets: false,
            CancellationToken.None).ConfigureAwait(false);
        CompletionItem stringBuilder = completion.Items.Single(
            static item => item.Label == "StringBuilder");
        IReadOnlyList<TextEdit>? additionalTextEdits = stringBuilder.AdditionalTextEdits;
        if (additionalTextEdits is null ||
            additionalTextEdits.Count != 1 ||
            !string.Equals(
                additionalTextEdits[0].NewText.TrimEnd('\r', '\n'),
                "@using System.Text",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Razor completion benchmark did not resolve its import fixture.");
        }
    }

    /// <summary>
    /// Measures repeated Razor import completion after generated-document mapping is indexed.
    /// </summary>
    [Benchmark]
    public Task<CompletionList> CachedImportCompletionAsync() =>
        _workspaceManager.GetCompletionsAsync(
            _parameters,
            supportsSnippets: false,
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
        @code {
            private StringBui
        }
        """;
}

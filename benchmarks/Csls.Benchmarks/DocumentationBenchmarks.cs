using BenchmarkDotNet.Attributes;
using Csls.Protocol;
using Csls.Workspaces;

namespace Csls.Benchmarks;

/// <summary>
/// Measures structured and inherited documentation returned by signature help.
/// </summary>
[BenchmarkCategory("Documentation", "SignatureHelp")]
[MemoryDiagnoser]
public class DocumentationBenchmarks : IAsyncDisposable
{
    private WorkspaceManager _workspaceManager = null!;
    private SignatureHelpParams _parameters = null!;
    private TextDocumentPositionParams _hoverParameters = null!;
    private string _fixturePath = null!;
    private int _disposeState;

    /// <summary>
    /// Loads a real project and primes inherited XML documentation resolution.
    /// </summary>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-documentation-benchmark-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_fixturePath);
        string documentPath = Path.Join(_fixturePath, "Program.cs");
        await File.WriteAllTextAsync(
            Path.Join(_fixturePath, "Fixture.csproj"),
            ProjectText).ConfigureAwait(false);
        await File.WriteAllTextAsync(documentPath, DocumentText).ConfigureAwait(false);

        _workspaceManager = BenchmarkWorkspaceManagerFactory.Create();
        await _workspaceManager.LoadAsync([_fixturePath], CancellationToken.None)
            .ConfigureAwait(false);
        await _workspaceManager.OpenDocumentAsync(
            new TextDocumentItem
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath),
                LanguageId = "csharp",
                Version = 1,
                Text = DocumentText
            },
            CancellationToken.None).ConfigureAwait(false);
        _parameters = new SignatureHelpParams
        {
            TextDocument = new TextDocumentIdentifier
            {
                Uri = DocumentUri.FromFileSystemPath(documentPath)
            },
            Position = GetPosition(
                DocumentText,
                "renderer.Render(new Widget",
                "renderer.Render(".Length),
            Context = new SignatureHelpContext
            {
                TriggerKind = SignatureHelpTriggerKind.Invoked
            }
        };
        _hoverParameters = new TextDocumentPositionParams
        {
            TextDocument = _parameters.TextDocument,
            Position = GetPosition(DocumentText, "Render(new Widget")
        };
        SignatureHelp? signatureHelp = await _workspaceManager
            .GetSignatureHelpAsync(
                _parameters,
                CancellationToken.None,
                supportsMarkdown: true)
            .ConfigureAwait(false);
        if (signatureHelp?.Signatures.SingleOrDefault()?.Documentation is null)
        {
            throw new InvalidOperationException(
                "The documentation benchmark did not resolve its fixture.");
        }

        _ = await _workspaceManager.GetHoverAsync(
            _hoverParameters,
            CancellationToken.None,
            supportsMarkdown: true).ConfigureAwait(false) ?? throw new InvalidOperationException(
                "The documentation benchmark did not resolve hover content.");
    }

    /// <summary>
    /// Measures cached Roslyn hover with supplemental XML documentation.
    /// </summary>
    [Benchmark]
    public Task<Hover?> CachedHoverDocumentationAsync() =>
        _workspaceManager.GetHoverAsync(
            _hoverParameters,
            CancellationToken.None,
            supportsMarkdown: true);

    /// <summary>
    /// Measures inherited callable and parameter documentation formatting.
    /// </summary>
    [Benchmark]
    public Task<SignatureHelp?> InheritedSignatureDocumentationAsync() =>
        _workspaceManager.GetSignatureHelpAsync(
            _parameters,
            CancellationToken.None,
            supportsMarkdown: true);

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

    private static Position GetPosition(
        string source,
        string marker,
        int relativeOffset = 0)
    {
        int offset = source.IndexOf(marker, StringComparison.Ordinal);
        if (offset < 0)
        {
            throw new InvalidDataException($"Marker '{marker}' was not found.");
        }

        offset += relativeOffset;
        int line = 0;
        int lineStart = 0;
        for (int index = 0; index < offset; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        return new Position(line, offset - lineStart);
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

        public sealed class Widget;

        public interface IRenderer
        {
            /// <summary>
            /// Renders a <see cref="Widget"/> through the configured output.
            /// </summary>
            /// <param name="value">Widget supplied by the caller.</param>
            /// <seealso href="https://example.com/renderer">Renderer guide</seealso>
            string Render(Widget value);
        }

        public sealed class Renderer : IRenderer
        {
            /// <inheritdoc />
            public string Render(Widget value) => value.ToString();
        }

        public static class Program
        {
            public static void Main()
            {
                Renderer renderer = new();
                string result = renderer.Render(new Widget());
                Console.WriteLine(result);
            }
        }
        """;
}

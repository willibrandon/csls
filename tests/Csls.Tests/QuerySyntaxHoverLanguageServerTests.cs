using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies LINQ query range variables through a real language-server worker.
/// </summary>
[TestClass]
public sealed class QuerySyntaxHoverLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Resolves hover information for a range variable introduced by a query clause.
    /// </summary>
    [TestMethod]
    public async Task QueryRangeVariableReturnsSemanticHover()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-query-hover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Query.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-query-hover-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            JsonElement hoverElement = await lsp.RequestHoverAsync(
                documentPath,
                GetLastPosition(DocumentText, "doubled"),
                TestContext.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The query range variable returned no hover.");
            Hover hover = hoverElement.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException("The query range variable returned invalid hover.");
            Assert.AreEqual("plaintext", hover.Contents.Kind);
            Assert.Contains("int", hover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("doubled", hover.Contents.Value, StringComparison.Ordinal);
            Assert.AreEqual(
                new LspRange(new Position(10, 19), new Position(10, 26)),
                hover.Range);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static Position GetLastPosition(string text, string value)
    {
        int offset = text.LastIndexOf(value, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, offset);
        int line = 0;
        int lineStart = 0;
        for (int index = 0; index < offset; index++)
        {
            if (text[index] == '\n')
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

        public static class Query
        {
            public static IReadOnlyList<int> Run()
            {
                var query =
                    from value in new[] { 1, 2, 3 }
                    let doubled = value * 2
                    where doubled > 2
                    select doubled;
                return [.. query];
            }
        }
        """;
}

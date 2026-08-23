using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies C# linked editing through a real language-server worker.
/// </summary>
[TestClass]
public sealed class LinkedEditingLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Links equal paired XML documentation names and rejects unrelated syntax.
    /// </summary>
    [TestMethod]
    public async Task LinkedEditingReturnsExactXmlDocumentationPairs()
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
            $"csls-linked-editing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-linked-editing-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(
                initialization
                    .GetProperty("capabilities")
                    .GetProperty("linkedEditingRangeProvider")
                    .GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            LinkedEditingRanges? customFromStart =
                await lsp.RequestLinkedEditingRangesAsync(
                    documentPath,
                    new Position(3, 15),
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(customFromStart);
            Assert.HasCount(2, customFromStart.Ranges);
            Assert.AreEqual(
                new LspRange(new Position(3, 10), new Position(3, 20)),
                customFromStart.Ranges[0]);
            Assert.AreEqual(
                new LspRange(new Position(3, 27), new Position(3, 37)),
                customFromStart.Ranges[1]);
            Assert.IsNull(customFromStart.WordPattern);

            LinkedEditingRanges? customFromEnd =
                await lsp.RequestLinkedEditingRangesAsync(
                    documentPath,
                    new Position(3, 32),
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(customFromEnd);
            Assert.AreSequenceEqual(
                customFromStart.Ranges.ToArray(),
                customFromEnd.Ranges.ToArray());

            LinkedEditingRanges? summary = await lsp.RequestLinkedEditingRangesAsync(
                documentPath,
                new Position(2, 8),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(summary);
            Assert.AreSequenceEqual(
                new[]
                {
                    new LspRange(new Position(2, 5), new Position(2, 12)),
                    new LspRange(new Position(4, 6), new Position(4, 13))
                },
                summary.Ranges.ToArray());

            JsonElement raw = await lsp.RequestLinkedEditingRangesJsonAsync(
                documentPath,
                new Position(3, 15),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(JsonValueKind.Object, raw.ValueKind);
            Assert.HasCount(2, raw.GetProperty("ranges").EnumerateArray().ToArray());
            Assert.IsFalse(raw.TryGetProperty("wordPattern", out _));

            Assert.IsNull(await lsp.RequestLinkedEditingRangesAsync(
                documentPath,
                new Position(7, 35),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsNull(await lsp.RequestLinkedEditingRangesAsync(
                documentPath,
                new Position(10, 8),
                TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsNull(await lsp.RequestLinkedEditingRangesAsync(
                documentPath,
                new Position(13, 7),
                TestContext.CancellationToken).ConfigureAwait(false));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
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

        /// <summary>
        /// Uses <custom-tag>text</custom-tag>.
        /// </summary>
        public sealed class Worker
        {
            public string Run() => "<summary>";
        }

        /// <first></second>
        public sealed class Mismatched;

        /// <see cref="string"/>
        public sealed class SelfClosing;
        """;
}

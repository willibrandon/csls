using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies semantic source navigation through a real language-server worker.
/// </summary>
[TestClass]
public sealed class NavigationLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Finds exact declarations, definitions, implementations, types, and references.
    /// </summary>
    [TestMethod]
    public async Task SemanticNavigationReturnsExactSourceLocations()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-navigation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Combine(fixturePath, "Program.cs");
            string advancedDocumentPath = Path.Combine(fixturePath, "Advanced.cs");
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                advancedDocumentPath,
                AdvancedDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-navigation-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement capabilities = initialization.GetProperty("capabilities");
            Assert.IsTrue(capabilities.GetProperty("definitionProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("declarationProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("typeDefinitionProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("implementationProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("selectionRangeProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("documentHighlightProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("referencesProvider").GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            IReadOnlyList<Location> definitions = await lsp.RequestDefinitionsAsync(
                documentPath,
                new Position(13, 16),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location definition = Assert.ContainsSingle(definitions);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), definition.Uri);
            Assert.AreEqual(new Position(4, 23), definition.Range.Start);
            Assert.AreEqual(new Position(4, 26), definition.Range.End);

            IReadOnlyList<Location> usages = await lsp.RequestReferencesAsync(
                documentPath,
                new Position(13, 16),
                includeDeclaration: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, usages);
            Assert.Contains(new Position(13, 15), usages.Select(static location => location.Range.Start));
            Assert.Contains(new Position(14, 15), usages.Select(static location => location.Range.Start));

            IReadOnlyList<Location> referencesWithDeclaration =
                await lsp.RequestReferencesAsync(
                    documentPath,
                    new Position(13, 16),
                    includeDeclaration: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(3, referencesWithDeclaration);
            Assert.Contains(
                definition.Range.Start,
                referencesWithDeclaration.Select(static location => location.Range.Start));

            IReadOnlyList<Location> declarations = await lsp.RequestDeclarationsAsync(
                advancedDocumentPath,
                new Position(19, 17),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location declaration = Assert.ContainsSingle(declarations);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(advancedDocumentPath), declaration.Uri);
            Assert.AreEqual(new Position(4, 9), declaration.Range.Start);
            Assert.AreEqual(new Position(4, 16), declaration.Range.End);

            IReadOnlyList<Location> typeDefinitions = await lsp.RequestTypeDefinitionsAsync(
                advancedDocumentPath,
                new Position(18, 17),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location typeDefinition = Assert.ContainsSingle(typeDefinitions);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(advancedDocumentPath), typeDefinition.Uri);
            Assert.AreEqual(new Position(2, 17), typeDefinition.Range.Start);
            Assert.AreEqual(new Position(2, 24), typeDefinition.Range.End);

            IReadOnlyList<Location> implementations = await lsp.RequestImplementationsAsync(
                advancedDocumentPath,
                new Position(4, 10),
                TestContext.CancellationToken).ConfigureAwait(false);
            Location implementation = Assert.ContainsSingle(implementations);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(advancedDocumentPath), implementation.Uri);
            Assert.AreEqual(new Position(9, 16), implementation.Range.Start);
            Assert.AreEqual(new Position(9, 23), implementation.Range.End);

            IReadOnlyList<SelectionRange> selectionRanges =
                await lsp.RequestSelectionRangesAsync(
                    advancedDocumentPath,
                    [new Position(19, 17), new Position(18, 17)],
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, selectionRanges);
            SelectionRange invocationSelection = selectionRanges[0];
            Assert.AreEqual(new Position(19, 15), invocationSelection.Range.Start);
            Assert.AreEqual(new Position(19, 22), invocationSelection.Range.End);
            Assert.IsNotNull(invocationSelection.Parent);
            Assert.AreEqual(new Position(19, 8), invocationSelection.Parent.Range.Start);
            Assert.AreEqual(new Position(19, 22), invocationSelection.Parent.Range.End);
            Assert.IsNotNull(invocationSelection.Parent.Parent);
            Assert.AreEqual(new Position(19, 24), invocationSelection.Parent.Parent.Range.End);
            Assert.AreEqual(new Position(18, 16), selectionRanges[1].Range.Start);
            Assert.AreEqual(new Position(18, 22), selectionRanges[1].Range.End);

            IReadOnlyList<DocumentHighlight> highlights =
                await lsp.RequestDocumentHighlightsAsync(
                    advancedDocumentPath,
                    new Position(18, 17),
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(4, highlights);
            Assert.AreEqual(
                DocumentHighlightKind.Text,
                highlights.Single(static highlight =>
                    highlight.Range.Start == new Position(18, 16)).Kind);
            Assert.AreEqual(
                DocumentHighlightKind.Read,
                highlights.Single(static highlight =>
                    highlight.Range.Start == new Position(19, 8)).Kind);
            Assert.AreEqual(
                DocumentHighlightKind.Write,
                highlights.Single(static highlight =>
                    highlight.Range.Start == new Position(20, 8)).Kind);
            Assert.AreEqual(
                DocumentHighlightKind.Read,
                highlights.Single(static highlight =>
                    highlight.Range.Start == new Position(21, 12)).Kind);

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

        public static class Target
        {
            public static void Run()
            {
            }
        }

        public static class Program
        {
            public static void Main()
            {
                Target.Run();
                Target.Run();
            }
        }
        """;

    private const string AdvancedDocumentText = """
        namespace Fixture;

        public interface IRunner
        {
            void Execute();
        }

        public sealed class Runner : IRunner
        {
            public void Execute()
            {
            }
        }

        public static class AdvancedProgram
        {
            public static void Run()
            {
                IRunner runner = new Runner();
                runner.Execute();
                runner = new Runner();
                _ = runner;
            }
        }
        """;
}

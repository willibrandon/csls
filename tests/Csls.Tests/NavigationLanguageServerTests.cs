using System.Runtime.CompilerServices;
using System.Text.Json;
using Csls.Protocol;

namespace Csls.Tests;

/// <summary>
/// Verifies source definition and reference navigation through a real language-server worker.
/// </summary>
[TestClass]
public sealed class NavigationLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Finds the exact declaration and both usages of one source method.
    /// </summary>
    [TestMethod]
    public async Task DefinitionAndReferencesReturnExactSourceLocations()
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
            await File.WriteAllTextAsync(
                Path.Combine(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
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
}

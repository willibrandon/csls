using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies C# document links through a real language-server worker.
/// </summary>
[TestClass]
public sealed class DocumentLinkLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Links active local directives and excludes inactive or missing targets.
    /// </summary>
    [TestMethod]
    public async Task DocumentLinksReturnExistingActiveDirectiveTargets()
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
            $"csls-document-links-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string loadPath = Path.Join(fixturePath, "Loaded.csx");
            string referencePath = Path.Join(fixturePath, "Reference.dll");
            string mappedPath = Path.Join(fixturePath, "Mapped.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                loadPath,
                "System.Console.WriteLine(\"loaded\");",
                TestContext.CancellationToken).ConfigureAwait(false);
            File.Copy(typeof(DocumentLinkLanguageServerTests).Assembly.Location, referencePath);
            await File.WriteAllTextAsync(
                mappedPath,
                "namespace Fixture;",
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-document-link-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement provider = initialization
                .GetProperty("capabilities")
                .GetProperty("documentLinkProvider");
            Assert.IsFalse(provider.GetProperty("resolveProvider").GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            IReadOnlyList<DocumentLink> links = await lsp.RequestDocumentLinksAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.HasCount(3, links);
            Assert.AreEqual(new LspRange(new Position(0, 7), new Position(0, 17)), links[0].Range);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(loadPath), links[0].Target);
            Assert.AreEqual(new LspRange(new Position(1, 4), new Position(1, 17)), links[1].Range);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(referencePath), links[1].Target);
            Assert.AreEqual(new LspRange(new Position(2, 11), new Position(2, 20)), links[2].Range);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(mappedPath), links[2].Target);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
        #load "Loaded.csx"
        #r "Reference.dll"
        #line 100 "Mapped.cs"
        #if false
        #load "Inactive.csx"
        #endif
        #load "Missing.csx"
        namespace Fixture;
        """;
}

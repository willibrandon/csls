using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies Razor semantic navigation through a real language-server worker process.
/// </summary>
[TestClass]
public sealed class RazorNavigationLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Finds external and Razor declarations across persisted, overlay, and reloaded snapshots.
    /// </summary>
    /// <param name="documentRelativePath">The Razor document path within the fixture.</param>
    /// <param name="importsRelativePath">The matching Razor imports path within the fixture.</param>
    /// <param name="membersDirective">The Razor directive that declares generated class members.</param>
    [TestMethod]
    [DataRow("Component.razor", "_Imports.razor", "code")]
    [DataRow("Pages/Index.cshtml", "Pages/_ViewImports.cshtml", "functions")]
    public async Task RazorNavigationTracksCurrentProjectSnapshot(
        string documentRelativePath,
        string importsRelativePath,
        string membersDirective)
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
            $"csls-razor-navigation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, documentRelativePath);
            string importsPath = Path.Join(fixturePath, importsRelativePath);
            string declarationsPath = Path.Join(fixturePath, "NavigationValues.cs");
            Directory.CreateDirectory(
                Path.GetDirectoryName(documentPath)
                    ?? throw new InvalidOperationException("The Razor fixture has no directory."));
            string persistedText = CreateRazorText(membersDirective, "LocalValue");
            string overlayText = CreateRazorText(membersDirective, "OverlayValue");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                declarationsPath,
                NavigationValuesText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                importsPath,
                "@using Fixture",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                persistedText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-razor-navigation-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, persistedText, "razor")
                .ConfigureAwait(false);

            Location localDefinition = Assert.ContainsSingle(
                await lsp.RequestDefinitionsAsync(
                    documentPath,
                    new Position(0, 8),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                localDefinition,
                documentPath,
                new Position(2, 19),
                new Position(2, 29));

            Location definition = Assert.ContainsSingle(
                await lsp.RequestDefinitionsAsync(
                    documentPath,
                    new Position(4, 6),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                definition,
                declarationsPath,
                new Position(12, 20),
                new Position(12, 25));

            Location declaration = Assert.ContainsSingle(
                await lsp.RequestDeclarationsAsync(
                    documentPath,
                    new Position(4, 13),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                declaration,
                declarationsPath,
                new Position(14, 28),
                new Position(14, 36));

            Location typeDefinition = Assert.ContainsSingle(
                await lsp.RequestTypeDefinitionsAsync(
                    documentPath,
                    new Position(4, 13),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                typeDefinition,
                declarationsPath,
                new Position(2, 17),
                new Position(2, 26));

            Location implementation = Assert.ContainsSingle(
                await lsp.RequestImplementationsAsync(
                    documentPath,
                    new Position(5, 8),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                implementation,
                declarationsPath,
                new Position(7, 20),
                new Position(7, 42));

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = overlayText }])
                .ConfigureAwait(false);
            Location overlayDefinition = Assert.ContainsSingle(
                await lsp.RequestDefinitionsAsync(
                    documentPath,
                    new Position(0, 8),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                overlayDefinition,
                documentPath,
                new Position(2, 19),
                new Position(2, 31));

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlWorkspaceOperationResult reload = await control.ReloadWorkspaceAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(reload.PreviousGeneration + 1, reload.CurrentGeneration);
            Location reloadedDefinition = Assert.ContainsSingle(
                await lsp.RequestDefinitionsAsync(
                    documentPath,
                    new Position(0, 8),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                reloadedDefinition,
                documentPath,
                new Position(2, 19),
                new Position(2, 31));

            await lsp.CloseDocumentAsync(documentPath).ConfigureAwait(false);
            Location restoredDefinition = Assert.ContainsSingle(
                await lsp.RequestDefinitionsAsync(
                    documentPath,
                    new Position(0, 8),
                    TestContext.CancellationToken).ConfigureAwait(false));
            AssertLocation(
                restoredDefinition,
                documentPath,
                new Position(2, 19),
                new Position(2, 29));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static void AssertLocation(
        Location location,
        string path,
        Position start,
        Position end)
    {
        Assert.AreEqual(DocumentUri.FromFileSystemPath(path), location.Uri);
        Assert.AreEqual(start, location.Range.Start);
        Assert.AreEqual(end, location.Range.End);
    }

    private static string CreateRazorText(string membersDirective, string localName) =>
        string.Join(
            Environment.NewLine,
            $"<p>@{localName}</p>",
            $"@{membersDirective} {{",
            $"    private string {localName} => Known.Contract.Value;",
            "}",
            "<p>@Known.Contract.Value</p>",
            "<p>@((IContract)new ContractImplementation()).Value</p>");

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string NavigationValuesText = """
        namespace Fixture;

        public interface IContract
        {
            string Value { get; }
        }

        public sealed class ContractImplementation : IContract
        {
            public string Value => "value";
        }

        public static class Known
        {
            public static IContract Contract { get; } = new ContractImplementation();
        }
        """;
}

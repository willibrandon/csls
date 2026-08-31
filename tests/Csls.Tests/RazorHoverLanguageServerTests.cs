using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies Razor hover through a real language-server worker process.
/// </summary>
[TestClass]
public sealed class RazorHoverLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Tracks component and view hover across overlays, reloads, and persisted text.
    /// </summary>
    /// <param name="documentRelativePath">The Razor document path within the fixture.</param>
    /// <param name="importsRelativePath">The matching Razor imports path within the fixture.</param>
    [TestMethod]
    [DataRow("Component.razor", "_Imports.razor")]
    [DataRow("Pages/Index.cshtml", "Pages/_ViewImports.cshtml")]
    public async Task RazorHoverTracksCurrentProjectSnapshot(
        string documentRelativePath,
        string importsRelativePath)
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
            $"csls-razor-hover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string componentPath = Path.Join(fixturePath, documentRelativePath);
            string importsPath = Path.Join(fixturePath, importsRelativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(componentPath)
                    ?? throw new InvalidOperationException("The Razor fixture has no directory."));
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "HoverValues.cs"),
                HoverValuesText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                importsPath,
                "@using Fixture",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                componentPath,
                PersistedRazorText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-razor-hover-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(componentPath, PersistedRazorText, "razor")
                .ConfigureAwait(false);

            Hover persistedHover = await RequestHoverAsync(
                lsp,
                componentPath,
                new Position(0, 12),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("plaintext", persistedHover.Contents.Kind);
            Assert.Contains("Known.Value", persistedHover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("persisted value", persistedHover.Contents.Value, StringComparison.Ordinal);
            Assert.AreEqual(
                new LspRange(new Position(0, 10), new Position(0, 15)),
                persistedHover.Range);
            Hover cachedHover = await RequestHoverAsync(
                lsp,
                componentPath,
                new Position(0, 12),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(persistedHover.Contents, cachedHover.Contents);
            Assert.AreEqual(persistedHover.Range, cachedHover.Range);

            await lsp.ChangeDocumentAsync(
                componentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = OverlayRazorText }])
                .ConfigureAwait(false);
            Hover overlayHover = await RequestHoverAsync(
                lsp,
                componentPath,
                new Position(0, 16),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("Alternate.Value", overlayHover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("overlay value", overlayHover.Contents.Value, StringComparison.Ordinal);
            Assert.AreEqual(
                new LspRange(new Position(0, 14), new Position(0, 19)),
                overlayHover.Range);

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlWorkspaceOperationResult reload = await control.ReloadWorkspaceAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(reload.PreviousGeneration + 1, reload.CurrentGeneration);
            Hover reloadedHover = await RequestHoverAsync(
                lsp,
                componentPath,
                new Position(0, 16),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("Alternate.Value", reloadedHover.Contents.Value, StringComparison.Ordinal);
            Assert.AreEqual(overlayHover.Range, reloadedHover.Range);

            await lsp.CloseDocumentAsync(componentPath).ConfigureAwait(false);
            Hover restoredHover = await RequestHoverAsync(
                lsp,
                componentPath,
                new Position(0, 12),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("Known.Value", restoredHover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("persisted value", restoredHover.Contents.Value, StringComparison.Ordinal);
            Assert.AreEqual(persistedHover.Range, restoredHover.Range);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static async Task<Hover> RequestHoverAsync(
        LspProcessSession lsp,
        string documentPath,
        Position position,
        CancellationToken cancellationToken)
    {
        JsonElement element = await lsp.RequestHoverAsync(
            documentPath,
            position,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Razor document returned no hover.");
        return element.Deserialize(LspJsonSerializerContext.Default.Hover)
            ?? throw new InvalidDataException("The Razor document returned invalid hover.");
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string HoverValuesText = """
        namespace Fixture;

        /// <summary>
        /// Supplies the value persisted on disk.
        /// </summary>
        public static class Known
        {
            /// <summary>
            /// Gets the persisted value.
            /// </summary>
            public static string Value => "known";
        }

        /// <summary>
        /// Supplies the value used by the unsaved overlay.
        /// </summary>
        public static class Alternate
        {
            /// <summary>
            /// Gets the overlay value.
            /// </summary>
            public static string Value => "alternate";
        }
        """;

    private const string PersistedRazorText = "<p>@Known.Value</p>";
    private const string OverlayRazorText = "<p>@Alternate.Value</p>";
}

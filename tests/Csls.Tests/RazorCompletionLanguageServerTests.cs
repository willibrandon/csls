using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies Razor C# completion through a real language-server worker process.
/// </summary>
[TestClass]
public sealed class RazorCompletionLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Returns mapped member edits from current component and view snapshots.
    /// </summary>
    /// <param name="documentRelativePath">The Razor document path within the fixture.</param>
    /// <param name="importsRelativePath">The matching Razor imports path within the fixture.</param>
    [TestMethod]
    [DataRow("Component.razor", "_Imports.razor")]
    [DataRow("Pages/Index.cshtml", "Pages/_ViewImports.cshtml")]
    public async Task RazorCompletionTracksCurrentProjectSnapshot(
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
            $"csls-razor-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, documentRelativePath);
            string importsPath = Path.Join(fixturePath, importsRelativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(documentPath)
                    ?? throw new InvalidOperationException("The Razor fixture has no directory."));
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Known.cs"),
                KnownText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                importsPath,
                "@using Fixture",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                PersistedText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-razor-completion-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, PersistedText, "razor")
                .ConfigureAwait(false);

            CompletionItem persisted = await GetCompletionAsync(
                lsp,
                documentPath,
                "WriteValue").ConfigureAwait(false);
            AssertCompletion(persisted, "WriteValue");
            CompletionItem resolved = await lsp.ResolveCompletionAsync(
                persisted,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolved.Documentation);
            Assert.Contains(
                "WriteValue",
                resolved.Documentation.Value,
                StringComparison.Ordinal);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = OverlayText }])
                .ConfigureAwait(false);
            CompletionItem overlay = await GetCompletionAsync(
                lsp,
                documentPath,
                "OverlayValue").ConfigureAwait(false);
            AssertCompletion(overlay, "OverlayValue");

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlWorkspaceOperationResult reload = await control.ReloadWorkspaceAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(reload.PreviousGeneration + 1, reload.CurrentGeneration);
            CompletionItem reloaded = await GetCompletionAsync(
                lsp,
                documentPath,
                "OverlayValue").ConfigureAwait(false);
            AssertCompletion(reloaded, "OverlayValue");

            await lsp.CloseDocumentAsync(documentPath).ConfigureAwait(false);
            CompletionItem restored = await GetCompletionAsync(
                lsp,
                documentPath,
                "WriteValue").ConfigureAwait(false);
            AssertCompletion(restored, "WriteValue");

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Converts Roslyn import completion changes into Razor using directives.
    /// </summary>
    /// <param name="documentRelativePath">The Razor document path within the fixture.</param>
    /// <param name="importsRelativePath">The matching Razor imports path within the fixture.</param>
    /// <param name="membersDirective">The directive that declares generated class members.</param>
    /// <param name="documentPrefix">The Razor directives that precede the completion position.</param>
    /// <param name="completionLine">The zero-based line containing the completion position.</param>
    /// <param name="importLine">The zero-based line where the import edit belongs.</param>
    [TestMethod]
    [DataRow("Component.razor", "_Imports.razor", "code", "", 1, 0)]
    [DataRow("Pages/Index.cshtml", "Pages/_ViewImports.cshtml", "functions", "@page\n", 2, 1)]
    public async Task RazorCompletionMapsImportEdits(
        string documentRelativePath,
        string importsRelativePath,
        string membersDirective,
        string documentPrefix,
        int completionLine,
        int importLine)
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
            $"csls-razor-import-completion-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, documentRelativePath);
            string importsPath = Path.Join(fixturePath, importsRelativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(documentPath)
                    ?? throw new InvalidOperationException("The Razor fixture has no directory."));
            string documentText = CreateImportText(documentPrefix, membersDirective);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                importsPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                documentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-razor-import-completion-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, documentText, "razor")
                .ConfigureAwait(false);

            CompletionList completion = await lsp.RequestCompletionAsync(
                documentPath,
                new Position(completionLine, 21),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem stringBuilder = completion.Items.Single(
                static item => item.Label == "StringBuilder");
            Assert.AreEqual(CompletionItemKind.Class, stringBuilder.Kind);
            Assert.IsNotNull(stringBuilder.TextEdit);
            Assert.AreEqual(
                new Position(completionLine, 12),
                stringBuilder.TextEdit.Range.Start);
            Assert.AreEqual(
                new Position(completionLine, 21),
                stringBuilder.TextEdit.Range.End);
            Assert.AreEqual("StringBuilder", stringBuilder.TextEdit.NewText);
            IReadOnlyList<TextEdit>? additionalTextEdits = stringBuilder.AdditionalTextEdits;
            Assert.IsNotNull(additionalTextEdits);
            TextEdit importEdit = Assert.ContainsSingle(additionalTextEdits);
            Assert.AreEqual(new Position(importLine, 0), importEdit.Range.Start);
            Assert.AreEqual(importEdit.Range.Start, importEdit.Range.End);
            Assert.AreEqual(
                $"@using System.Text{GetNewLine(documentText)}",
                importEdit.NewText);
            CompletionItem resolved = await lsp.ResolveCompletionAsync(
                stringBuilder,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolved.Documentation);
            Assert.Contains(
                "StringBuilder",
                resolved.Documentation.Value,
                StringComparison.Ordinal);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task<CompletionItem> GetCompletionAsync(
        LspProcessSession lsp,
        string documentPath,
        string label)
    {
        CompletionList completion = await lsp.RequestCompletionAsync(
            documentPath,
            new Position(0, 13),
            TestContext.CancellationToken).ConfigureAwait(false);
        return completion.Items.Single(item => item.Label == label);
    }

    private static void AssertCompletion(CompletionItem item, string replacement)
    {
        Assert.AreEqual(CompletionItemKind.Property, item.Kind);
        Assert.IsNotNull(item.TextEdit);
        Assert.AreEqual(new Position(0, 10), item.TextEdit.Range.Start);
        Assert.AreEqual(new Position(0, 13), item.TextEdit.Range.End);
        Assert.AreEqual(replacement, item.TextEdit.NewText);
        Assert.IsNull(item.AdditionalTextEdits);
        Assert.IsNotNull(item.Data);
    }

    private static string CreateImportText(string prefix, string membersDirective) =>
        string.Concat(
            prefix,
            $$"""
            @{{membersDirective}} {
                private StringBui
            }
            """);

    private static string GetNewLine(string text) =>
        text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string KnownText = """
        namespace Fixture;

        /// <summary>
        /// Supplies completion values to Razor fixtures.
        /// </summary>
        public static class Known
        {
            /// <summary>
            /// Gets the persisted completion value.
            /// </summary>
            public static string WriteValue => "persisted";

            /// <summary>
            /// Gets the overlay completion value.
            /// </summary>
            public static string OverlayValue => "overlay";
        }
        """;

    private const string PersistedText = "<p>@Known.Wri</p>";
    private const string OverlayText = "<p>@Known.Ove</p>";
}

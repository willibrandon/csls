using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies Roslyn completion candidates and commit edits through a real language-server worker.
/// </summary>
[TestClass]
public sealed class CompletionLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Returns exact member edits and unimported-type edits through LSP completion.
    /// </summary>
    [TestMethod]
    public async Task CompletionReturnsMemberAndImportTextEdits()
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
            $"csls-completion-{Guid.NewGuid():N}");
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
                MemberDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-completion-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement completionProvider = initialization
                .GetProperty("capabilities")
                .GetProperty("completionProvider");
            Assert.IsFalse(completionProvider.GetProperty("resolveProvider").GetBoolean());
            Assert.Contains(
                ".",
                completionProvider
                    .GetProperty("triggerCharacters")
                    .EnumerateArray()
                    .Select(static character => character.GetString()));
            await lsp.OpenDocumentAsync(documentPath, MemberDocumentText).ConfigureAwait(false);

            CompletionList memberCompletion = await lsp.RequestCompletionAsync(
                documentPath,
                new Position(6, 19),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem writeLine = memberCompletion.Items.Single(
                static item => item.Label == "WriteLine");
            Assert.AreEqual(CompletionItemKind.Method, writeLine.Kind);
            Assert.IsNotNull(writeLine.TextEdit);
            Assert.AreEqual(new Position(6, 16), writeLine.TextEdit.Range.Start);
            Assert.AreEqual(new Position(6, 19), writeLine.TextEdit.Range.End);
            Assert.AreEqual("WriteLine", writeLine.TextEdit.NewText);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = ImportDocumentText }])
                .ConfigureAwait(false);
            CompletionList importCompletion = await lsp.RequestCompletionAsync(
                documentPath,
                new Position(6, 17),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem stringBuilder = importCompletion.Items.Single(
                static item => item.Label == "StringBuilder");
            Assert.AreEqual(CompletionItemKind.Class, stringBuilder.Kind);
            Assert.IsNotNull(stringBuilder.TextEdit);
            Assert.AreEqual("StringBuilder", stringBuilder.TextEdit.NewText);
            Assert.IsNotNull(stringBuilder.AdditionalTextEdits);
            string additionalText = string.Concat(
                stringBuilder.AdditionalTextEdits.Select(static edit => edit.NewText));
            Assert.Contains(
                "using System.Text",
                additionalText,
                "The StringBuilder completion did not include its required using directive.");

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

    private const string MemberDocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                Console.Wri
            }
        }
        """;

    private const string ImportDocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                StringBui
            }
        }
        """;
}

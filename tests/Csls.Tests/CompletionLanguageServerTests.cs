using Csls.Protocol;
using StreamJsonRpc;
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
            Assert.IsTrue(completionProvider.GetProperty("resolveProvider").GetBoolean());
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
            Assert.IsNull(writeLine.InsertTextFormat);
            Assert.IsNotNull(writeLine.Data);
            CompletionItem resolvedWriteLine = await lsp.ResolveCompletionAsync(
                writeLine,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(writeLine.TextEdit, resolvedWriteLine.TextEdit);
            Assert.IsNotNull(resolvedWriteLine.Documentation);
            Assert.AreEqual("plaintext", resolvedWriteLine.Documentation.Kind);
            Assert.Contains(
                "WriteLine",
                resolvedWriteLine.Documentation.Value,
                StringComparison.Ordinal);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = ImportDocumentText }])
                .ConfigureAwait(false);
            RemoteInvocationException staleResolve =
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                    () => lsp.ResolveCompletionAsync(
                        writeLine,
                        TestContext.CancellationToken)).ConfigureAwait(false);
            Assert.Contains(
                "workspace changed",
                staleResolve.Message,
                StringComparison.OrdinalIgnoreCase);
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
            CompletionItem resolvedStringBuilder = await lsp.ResolveCompletionAsync(
                stringBuilder,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolvedStringBuilder.Documentation);
            Assert.Contains(
                "StringBuilder",
                resolvedStringBuilder.Documentation.Value,
                StringComparison.Ordinal);
            Assert.IsNotNull(resolvedStringBuilder.AdditionalTextEdits);
            Assert.HasCount(
                stringBuilder.AdditionalTextEdits.Count,
                resolvedStringBuilder.AdditionalTextEdits);
            Assert.AreEqual(
                stringBuilder.AdditionalTextEdits[0],
                resolvedStringBuilder.AdditionalTextEdits[0]);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 3,
                [new TextDocumentContentChangeEvent { Text = SnippetDocumentText }])
                .ConfigureAwait(false);
            CompletionList plainCompletion = await lsp.RequestCompletionAsync(
                documentPath,
                new Position(6, 10),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem plainForSnippet = plainCompletion.Items.Single(
                static item => item.Label == "for" &&
                    item.Kind == CompletionItemKind.Snippet);
            Assert.IsNull(plainForSnippet.InsertTextFormat);
            Assert.IsNotNull(plainForSnippet.TextEdit);
            Assert.DoesNotContain(
                "$0",
                plainForSnippet.TextEdit.NewText,
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

    /// <summary>
    /// Emits Roslyn snippet edits only when the real LSP client advertises snippet support.
    /// </summary>
    [TestMethod]
    public async Task CompletionHonorsClientSnippetSupport()
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
            $"csls-completion-snippet-{Guid.NewGuid():N}");
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
                SnippetDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-completion-snippet-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse("""
                {
                  "textDocument": {
                    "completion": {
                      "completionItem": {
                        "snippetSupport": true,
                        "resolveSupport": {
                          "properties": ["detail", "documentation"]
                        }
                      }
                    }
                  }
                }
                """);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(
                initialization
                    .GetProperty("capabilities")
                    .GetProperty("completionProvider")
                    .GetProperty("resolveProvider")
                    .GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, SnippetDocumentText).ConfigureAwait(false);

            CompletionList completion = await lsp.RequestCompletionAsync(
                documentPath,
                new Position(6, 10),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem forSnippet = completion.Items.Single(
                static item => item.Label == "for" &&
                    item.Kind == CompletionItemKind.Snippet);
            Assert.AreEqual(InsertTextFormat.Snippet, forSnippet.InsertTextFormat);
            Assert.IsNotNull(forSnippet.TextEdit);
            Assert.Contains("$0", forSnippet.TextEdit.NewText, StringComparison.Ordinal);
            Assert.IsNotNull(forSnippet.Data);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = MethodSnippetDocumentText }])
                .ConfigureAwait(false);
            CompletionList methodCompletion = await lsp.RequestCompletionAsync(
                documentPath,
                new Position(6, 14),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem calculate = methodCompletion.Items.Single(
                static item => item.Label == "Calculate");
            Assert.AreEqual(CompletionItemKind.Method, calculate.Kind);
            Assert.AreEqual(InsertTextFormat.Snippet, calculate.InsertTextFormat);
            Assert.IsNotNull(calculate.TextEdit);
            Assert.AreEqual(
                "Calculate(${1:value}, ${2:text})$0",
                calculate.TextEdit.NewText);
            Assert.DoesNotContain("optional", calculate.TextEdit.NewText, StringComparison.Ordinal);

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

    private const string SnippetDocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                fo
            }
        }
        """;

    private const string MethodSnippetDocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static void Main()
            {
                Calcul
            }

            private static int Calculate(int value, string text, int optional = 0) =>
                value + text.Length + optional;
        }
        """;
}

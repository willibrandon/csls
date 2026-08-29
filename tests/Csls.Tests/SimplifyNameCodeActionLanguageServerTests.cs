using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies semantic name simplification through a real language-server worker.
/// </summary>
[TestClass]
public sealed class SimplifyNameCodeActionLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Simplifies an imported qualified name without changing its bound symbol.
    /// </summary>
    [TestMethod]
    public async Task ImportedQualifiedNameProducesSemanticQuickFix()
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
            $"csls-simplify-name-{Guid.NewGuid():N}");
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
                "csls-simplify-name-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "quickfix",
                initialization
                    .GetProperty("capabilities")
                    .GetProperty("codeActionProvider")
                    .GetProperty("codeActionKinds")
                    .EnumerateArray()
                    .Select(static kind => kind.GetString()));
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            var targetRange = new LspRange(new Position(8, 8), new Position(8, 22));
            IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
                documentPath,
                targetRange,
                only: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction action = Assert.ContainsSingle(actions.Where(static candidate =>
                candidate.Title.Contains("Simplify member access", StringComparison.Ordinal)));
            Assert.AreEqual("Simplify member access 'System.Console'", action.Title);
            Assert.AreEqual("quickfix", action.Kind);
            Assert.IsNull(action.IsPreferred);
            WorkspaceEdit edit = action.Edit
                ?? throw new InvalidDataException("The simplify-name action had no edit.");
            TextDocumentEdit documentEdit = Assert.ContainsSingle(
                edit.DocumentChanges.OfType<TextDocumentEdit>());
            Assert.AreEqual(1, documentEdit.TextDocument.Version);
            Assert.AreEqual(
                DocumentText.Replace("System.Console", "Console", StringComparison.Ordinal),
                ApplyTextEdits(DocumentText, documentEdit.Edits));

            string workerDiagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                workerDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static string ApplyTextEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        var sourceText = SourceText.From(text);
        IEnumerable<TextChange> changes = edits.Select(edit => new TextChange(
            TextSpan.FromBounds(
                GetOffset(sourceText, edit.Range.Start),
                GetOffset(sourceText, edit.Range.End)),
            edit.NewText));
        return sourceText.WithChanges(changes).ToString();
    }

    private static int GetOffset(SourceText text, Position position) =>
        text.Lines[position.Line].Start + position.Character;

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <ImplicitUsings>disable</ImplicitUsings>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        using System;

        namespace Fixture;

        internal static class Program
        {
            public static void Write()
            {
                System.Console.WriteLine("hello");
            }
        }
        """;
}

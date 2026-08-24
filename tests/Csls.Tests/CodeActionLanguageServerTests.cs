using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies semantic quick fixes through a real language-server worker and Roslyn workspace.
/// </summary>
[TestClass]
public sealed class CodeActionLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Adds a verified missing namespace import and clears the originating compiler diagnostic.
    /// </summary>
    [TestMethod]
    public async Task MissingUsingQuickFixProducesVersionedEdit()
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
            $"csls-code-actions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            string ambiguousPath = Path.Join(fixturePath, "Ambiguous.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                ambiguousPath,
                AmbiguousDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-code-action-worker",
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
            await lsp.OpenDocumentAsync(ambiguousPath, AmbiguousDocumentText).ConfigureAwait(false);

            DocumentDiagnosticReport diagnosticReport = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> diagnostics = diagnosticReport.Items
                ?? throw new InvalidDataException("The diagnostic report had no items.");
            Diagnostic missingType = diagnostics.Single(static diagnostic =>
                diagnostic.Code == "CS0246" &&
                diagnostic.Message.Contains("StringBuilder", StringComparison.Ordinal));
            var targetRange = new LspRange(new Position(6, 26), new Position(6, 39));
            Assert.AreEqual(targetRange, missingType.Range);

            IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
                documentPath,
                targetRange,
                ["quickfix"],
                [missingType],
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction action = Assert.ContainsSingle(actions);
            Assert.AreEqual("Add using System.Text", action.Title);
            Assert.AreEqual("quickfix", action.Kind);
            Assert.IsTrue(action.IsPreferred);
            Diagnostic attachedDiagnostic = Assert.ContainsSingle(action.Diagnostics ?? []);
            Assert.AreEqual("CS0246", attachedDiagnostic.Code);
            WorkspaceEdit edit = action.Edit
                ?? throw new InvalidDataException("The missing-using action had no edit.");
            TextDocumentEdit documentEdit = Assert.ContainsSingle(
                edit.DocumentChanges.OfType<TextDocumentEdit>());
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), documentEdit.TextDocument.Uri);
            Assert.AreEqual(1, documentEdit.TextDocument.Version);
            string changedText = ApplyTextEdits(DocumentText, documentEdit.Edits);
            Assert.StartsWith("using System.Text;", changedText, StringComparison.Ordinal);
            Assert.Contains("new StringBuilder()", changedText, StringComparison.Ordinal);

            IReadOnlyList<CodeAction> unrelatedActions = await lsp.RequestCodeActionsAsync(
                documentPath,
                new LspRange(new Position(0, 0), new Position(0, 0)),
                ["quickfix"],
                [missingType],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(unrelatedActions);

            DocumentDiagnosticReport ambiguousDiagnosticReport =
                await lsp.RequestDiagnosticsAsync(
                    ambiguousPath,
                    previousResultId: null,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Diagnostic ambiguousType = ambiguousDiagnosticReport.Items?
                .Single(static diagnostic =>
                    diagnostic.Code == "CS0246" &&
                    diagnostic.Message.Contains("Timer", StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    "The ambiguous-type diagnostic report had no Timer diagnostic.");
            IReadOnlyList<CodeAction> ambiguousActions = await lsp.RequestCodeActionsAsync(
                ambiguousPath,
                ambiguousType.Range,
                ["quickfix"],
                [ambiguousType],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, ambiguousActions);
            Assert.AreEqual("Add using System.Threading", ambiguousActions[0].Title);
            Assert.AreEqual("Add using System.Timers", ambiguousActions[1].Title);
            Assert.IsTrue(ambiguousActions.All(static action => action.IsPreferred != true));
            Assert.IsTrue(ambiguousActions.All(static action =>
                action.Edit is { DocumentChanges.Count: > 0 }));

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = changedText }]).ConfigureAwait(false);
            DocumentDiagnosticReport fixedReport = await lsp.RequestDiagnosticsAsync(
                documentPath,
                diagnosticReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> fixedDiagnostics = fixedReport.Items
                ?? throw new InvalidDataException("The updated diagnostic report had no items.");
            Assert.DoesNotContain(
                "CS0246",
                fixedDiagnostics.Select(static diagnostic => diagnostic.Code));

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
        var sourceText = SourceText.From(text, Encoding.UTF8);
        IEnumerable<TextChange> changes = edits.Select(edit => new TextChange(
            TextSpan.FromBounds(
                GetOffset(sourceText, edit.Range.Start),
                GetOffset(sourceText, edit.Range.End)),
            edit.NewText));
        return sourceText.WithChanges(changes).ToString();
    }

    private static int GetOffset(
        SourceText text,
        Position position) => text.Lines[position.Line].Start + position.Character;

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>disable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Program
        {
            public static string Build()
            {
                var builder = new StringBuilder();
                return builder.ToString();
            }
        }
        """;

    private const string AmbiguousDocumentText = """
        namespace Fixture;

        public static class Ambiguous
        {
            public static Timer? Value { get; }
        }
        """;
}

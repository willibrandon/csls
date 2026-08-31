using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;
using System.Runtime.CompilerServices;
using System.Text;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies interface implementation edits through a real language-server worker and SDK project.
/// </summary>
[TestClass]
public sealed class ImplementInterfaceCodeActionLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Implements inherited methods, properties, indexers, and events without copying default members.
    /// </summary>
    [TestMethod]
    public async Task ImplementInterfaceProducesCompilingVersionedEdit()
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
            $"csls-implement-interface-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Worker.cs");
            string operatorPath = Path.Join(fixturePath, "Factory.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                operatorPath,
                OperatorDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-implement-interface-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(operatorPath, OperatorDocumentText).ConfigureAwait(false);

            DocumentDiagnosticReport diagnosticReport = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> diagnostics = diagnosticReport.Items
                ?? throw new InvalidDataException("The diagnostic report had no items.");
            Diagnostic[] missingMembers =
            [
                .. diagnostics.Where(static diagnostic => diagnostic.Code == "CS0535")
            ];
            Assert.HasCount(4, missingMembers);

            var interfaceRange = new LspRange(
                new Position(16, 29),
                new Position(16, 41));
            IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
                documentPath,
                interfaceRange,
                ["quickfix"],
                missingMembers,
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction action = Assert.ContainsSingle(actions.Where(static action =>
                action.Title == "Implement interface"));
            Assert.AreEqual("Implement interface", action.Title);
            Assert.AreEqual("quickfix", action.Kind);
            Assert.IsNull(action.IsPreferred);
            Assert.HasCount(4, action.Diagnostics ?? []);
            WorkspaceEdit edit = action.Edit
                ?? throw new InvalidDataException("The implementation action had no edit.");
            TextDocumentEdit documentEdit = Assert.ContainsSingle(
                edit.DocumentChanges.OfType<TextDocumentEdit>());
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), documentEdit.TextDocument.Uri);
            Assert.AreEqual(1, documentEdit.TextDocument.Version);
            string changedText = ApplyTextEdits(DocumentText, documentEdit.Edits);
            Assert.Contains("public int Count", changedText, StringComparison.Ordinal);
            Assert.Contains("public void Reset()", changedText, StringComparison.Ordinal);
            Assert.AreEqual(
                changedText.IndexOf("public void Reset()", StringComparison.Ordinal),
                changedText.LastIndexOf("public void Reset()", StringComparison.Ordinal));
            Assert.Contains("public event EventHandler? Changed", changedText, StringComparison.Ordinal);
            Assert.Contains("public int this[int index]", changedText, StringComparison.Ordinal);
            Assert.Contains(
                "public ValueTask<int> RunAsync<TState>(int value, TState state)",
                changedText,
                StringComparison.Ordinal);
            Assert.Contains("where TState : notnull", changedText, StringComparison.Ordinal);
            Assert.Contains("throw new NotImplementedException();", changedText, StringComparison.Ordinal);
            Assert.DoesNotContain("public string Description", changedText, StringComparison.Ordinal);

            IReadOnlyList<CodeAction> unrelatedActions = await lsp.RequestCodeActionsAsync(
                documentPath,
                new LspRange(new Position(0, 0), new Position(0, 0)),
                ["quickfix"],
                missingMembers,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(unrelatedActions);

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
                "CS0535",
                fixedDiagnostics.Select(static diagnostic => diagnostic.Code));
            Assert.DoesNotContain(
                DiagnosticSeverity.Error,
                fixedDiagnostics.Select(static diagnostic => diagnostic.Severity));

            DocumentDiagnosticReport operatorDiagnosticReport =
                await lsp.RequestDiagnosticsAsync(
                    operatorPath,
                    previousResultId: null,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Diagnostic[] operatorDiagnostics =
            [
                .. (operatorDiagnosticReport.Items ?? []).Where(
                    static diagnostic => diagnostic.Code == "CS0535")
            ];
            Assert.HasCount(2, operatorDiagnostics);
            IReadOnlyList<CodeAction> operatorActions = await lsp.RequestCodeActionsAsync(
                operatorPath,
                new LspRange(new Position(8, 30), new Position(8, 47)),
                ["quickfix"],
                operatorDiagnostics,
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction operatorAction = Assert.ContainsSingle(operatorActions.Where(
                static action => action.Title == "Implement interface"));
            TextDocumentEdit operatorEdit = Assert.ContainsSingle(
                operatorAction.Edit?.DocumentChanges.OfType<TextDocumentEdit>() ?? []);
            string operatorText = ApplyTextEdits(OperatorDocumentText, operatorEdit.Edits);
            Assert.Contains(
                "public static Factory Create()",
                operatorText,
                StringComparison.Ordinal);
            Assert.Contains(
                "public static Factory operator +(Factory left, Factory right)",
                operatorText,
                StringComparison.Ordinal);
            Assert.Contains(
                "throw new NotImplementedException();",
                operatorText,
                StringComparison.Ordinal);
            await lsp.ChangeDocumentAsync(
                operatorPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = operatorText }]).ConfigureAwait(false);
            DocumentDiagnosticReport fixedOperatorReport = await lsp.RequestDiagnosticsAsync(
                operatorPath,
                operatorDiagnosticReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<Diagnostic> fixedOperatorDiagnostics = fixedOperatorReport.Items
                ?? throw new InvalidDataException(
                    "The updated operator diagnostic report had no items.");
            Assert.DoesNotContain(
                "CS0535",
                fixedOperatorDiagnostics.Select(static diagnostic => diagnostic.Code));
            Assert.DoesNotContain(
                DiagnosticSeverity.Error,
                fixedOperatorDiagnostics.Select(static diagnostic => diagnostic.Severity));

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

    private static int GetOffset(SourceText text, Position position) =>
        text.Lines[position.Line].Start + position.Character;

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public interface IBase
        {
            int Count { get; }
            void Reset();
        }

        public interface IWorker<T> : IBase
        {
            event EventHandler? Changed;
            T this[int index] { get; set; }
            ValueTask<T> RunAsync<TState>(T value, TState state) where TState : notnull;
            string Description() => "default";
        }

        public sealed class Worker : IWorker<int>
        {
            public void Reset()
            {
            }
        }
        """;

    private const string OperatorDocumentText = """
        namespace FactoryFixture;

        public interface IFactory<TSelf> where TSelf : IFactory<TSelf>
        {
            static abstract TSelf Create();
            static abstract TSelf operator +(TSelf left, TSelf right);
        }

        public sealed class Factory : IFactory<Factory>
        {
        }
        """;
}

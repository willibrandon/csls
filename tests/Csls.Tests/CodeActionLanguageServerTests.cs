using Csls.Protocol;
using Microsoft.CodeAnalysis.Text;
using System.Diagnostics;
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
    /// Extracts a base class from a static class through the real language-server worker.
    /// </summary>
    [TestMethod]
    public async Task StaticClassProvidesEnabledExtractBaseClassAction()
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
            $"csls-static-refactoring-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Adapter.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                StaticRefactoringDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-static-refactoring-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await lsp.OpenDocumentAsync(
                documentPath,
                StaticRefactoringDocumentText).ConfigureAwait(false);

            Position[] positions =
            [
                new Position(0, 24),
                new Position(4, 21),
                new Position(6, 32),
                new Position(7, 12),
                new Position(9, 30),
                new Position(10, 10)
            ];
            Task<IReadOnlyList<CodeAction>>[] requests =
            [
                .. Enumerable.Range(0, 32).Select(index =>
                {
                    Position position = positions[index % positions.Length];
                    return lsp.RequestCodeActionsAsync(
                        documentPath,
                        new LspRange(position, position),
                        only: null,
                        TestContext.CancellationToken);
                })
            ];
            IReadOnlyList<CodeAction>[] actions = await Task.WhenAll(requests)
                .ConfigureAwait(false);
            Assert.HasCount(32, actions);
            Position openingBracePosition = new(5, 0);
            IReadOnlyList<CodeAction> openingBraceActions =
                await lsp.RequestCodeActionsAsync(
                    documentPath,
                    new LspRange(openingBracePosition, openingBracePosition),
                    only: null,
                    TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction extractBaseClass = Assert.ContainsSingle(
                openingBraceActions.Where(static action =>
                    action.Title == "Extract base class..."),
                string.Join(
                    Environment.NewLine,
                    openingBraceActions.Select(static action => action.Title)));
            Assert.AreEqual("refactor", extractBaseClass.Kind);
            WorkspaceEdit edit = extractBaseClass.Edit
                ?? throw new InvalidDataException(
                    "Extract Base Class did not provide a workspace edit.");
            TextDocumentEdit sourceEdit = Assert.ContainsSingle(
                edit.DocumentChanges.OfType<TextDocumentEdit>());
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(documentPath),
                sourceEdit.TextDocument.Uri);
            string sourceText = ApplyTextEdits(
                StaticRefactoringDocumentText,
                sourceEdit.Edits);
            Assert.Contains("static class NewBaseType", sourceText, StringComparison.Ordinal);
            Assert.Contains("static readonly Lazy", sourceText, StringComparison.Ordinal);
            Assert.Contains("static (ConstructorInfo", sourceText, StringComparison.Ordinal);
            Assert.Contains(
                "static class RoslynExtractBaseClassCodeRefactoringAdapter : NewBaseType",
                sourceText,
                StringComparison.Ordinal);
            Assert.IsNotEmpty(sourceEdit.Edits);

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

    /// <summary>
    /// Renames a symbol that violates an editorconfig naming rule and clears IDE1006.
    /// </summary>
    [TestMethod]
    public async Task NamingRuleQuickFixRenamesSymbol()
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
            $"csls-naming-code-action-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                NamingProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, ".editorconfig"),
                NamingEditorConfigText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                NamingDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await RestoreFixtureAsync(fixturePath).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-naming-code-action-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, NamingDocumentText).ConfigureAwait(false);

            DocumentDiagnosticReport diagnosticReport = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Diagnostic namingDiagnostic = diagnosticReport.Items?
                .Single(static diagnostic => diagnostic.Code == "IDE1006")
                ?? throw new InvalidDataException(
                    "The diagnostic report had no IDE1006 naming diagnostic.");
            Assert.AreEqual(4, namingDiagnostic.Range.Start.Line);

            IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
                documentPath,
                namingDiagnostic.Range,
                ["quickfix"],
                [namingDiagnostic],
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction action = Assert.ContainsSingle(actions.Where(static action =>
                action.Title == "Fix name violation: s_pollInterval"));
            Assert.AreEqual("quickfix", action.Kind);
            Assert.IsNull(action.IsPreferred);
            WorkspaceEdit edit = action.Edit
                ?? throw new InvalidDataException("The naming action had no edit.");
            TextDocumentEdit documentEdit = Assert.ContainsSingle(
                edit.DocumentChanges.OfType<TextDocumentEdit>());
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), documentEdit.TextDocument.Uri);
            Assert.HasCount(2, documentEdit.Edits);
            string changedText = ApplyTextEdits(NamingDocumentText, documentEdit.Edits);
            Assert.Contains("int s_pollInterval = 25;", changedText, StringComparison.Ordinal);
            Assert.Contains("Value => s_pollInterval;", changedText, StringComparison.Ordinal);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = changedText }]).ConfigureAwait(false);
            DocumentDiagnosticReport fixedReport = await lsp.RequestDiagnosticsAsync(
                documentPath,
                diagnosticReport.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "IDE1006",
                fixedReport.Items?.Select(static diagnostic => diagnostic.Code) ?? []);

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

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-code-action-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
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
            Assert.Contains(
                "System.Text.StringBuilder",
                actions.Select(static action => action.Title));
            CodeAction action = Assert.ContainsSingle(actions.Where(static action =>
                action.Title == "using System.Text;"));
            Assert.AreEqual("quickfix", action.Kind);
            Assert.IsNull(action.IsPreferred);
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
            CodeAction[] ambiguousImportActions =
            [
                .. ambiguousActions.Where(static action =>
                    action.Title.StartsWith("using ", StringComparison.Ordinal))
            ];
            Assert.HasCount(2, ambiguousImportActions);
            Assert.AreEqual("using System.Threading;", ambiguousImportActions[0].Title);
            Assert.AreEqual("using System.Timers;", ambiguousImportActions[1].Title);
            Assert.IsTrue(ambiguousImportActions.All(static action =>
                action.IsPreferred != true));
            Assert.IsTrue(ambiguousImportActions.All(static action =>
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

    private async Task RestoreFixtureAsync(string fixturePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = EditorToolResolver.ResolveDotNetHost(),
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = fixturePath
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add("Fixture.csproj");
        startInfo.ArgumentList.Add("--nologo");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The naming fixture restore did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(
            TestContext.CancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(
            TestContext.CancellationToken);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        string output = await outputTask.ConfigureAwait(false);
        string error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"Naming fixture restore failed.{Environment.NewLine}{output}{Environment.NewLine}{error}");
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

    private const string StaticRefactoringDocumentText = """
        using System.Reflection;

        namespace Fixture;

        internal static class RoslynExtractBaseClassCodeRefactoringAdapter
        {
            private static readonly Lazy<(ConstructorInfo Constructor, MethodInfo Method)> Contract =
                new(CreateContract, LazyThreadSafetyMode.ExecutionAndPublication);

            private static (ConstructorInfo Constructor, MethodInfo Method) CreateContract() =>
                (typeof(string).GetConstructors()[0], typeof(string).GetMethods()[0]);
        }
        """;

    private const string NamingDocumentText = """
        namespace Fixture;

        public static class Program
        {
            private static readonly int PollInterval = 25;

            public static int Value => PollInterval;
        }
        """;

    private const string NamingProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <EnableNETAnalyzers>true</EnableNETAnalyzers>
            <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
            <AnalysisLevel>latest</AnalysisLevel>
            <AnalysisMode>AllEnabledByDefault</AnalysisMode>
          </PropertyGroup>
        </Project>
        """;

    private const string NamingEditorConfigText = """
        root = true

        [*.cs]
        dotnet_diagnostic.IDE1006.severity = error
        dotnet_naming_rule.private_static_fields_should_be_prefixed.symbols = private_static_fields
        dotnet_naming_rule.private_static_fields_should_be_prefixed.style = private_static_fields_style
        dotnet_naming_rule.private_static_fields_should_be_prefixed.severity = error
        dotnet_naming_symbols.private_static_fields.applicable_kinds = field
        dotnet_naming_symbols.private_static_fields.applicable_accessibilities = private
        dotnet_naming_symbols.private_static_fields.required_modifiers = static
        dotnet_naming_style.private_static_fields_style.required_prefix = s_
        dotnet_naming_style.private_static_fields_style.capitalization = camel_case
        """;
}

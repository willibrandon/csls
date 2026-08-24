using Csls.Protocol;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies rename, formatting, code actions, and close synchronization through a real worker.
/// </summary>
[TestClass]
public sealed class EditLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Returns version-safe semantic edits and restores persisted text after document close.
    /// </summary>
    [TestMethod]
    public async Task SemanticEditsUseRoslynAndVersionedDocuments()
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
            $"csls-edits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string programPath = Path.Join(fixturePath, "Program.cs");
            string consumerPath = Path.Join(fixturePath, "Consumer.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                programPath,
                ProgramText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                consumerPath,
                ConsumerText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-edit-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement capabilities = initialization.GetProperty("capabilities");
            Assert.IsTrue(capabilities.GetProperty("renameProvider").GetProperty(
                "prepareProvider").GetBoolean());
            Assert.IsTrue(capabilities.GetProperty("documentFormattingProvider").GetBoolean());
            Assert.IsTrue(
                capabilities.GetProperty("documentRangeFormattingProvider").GetBoolean());
            JsonElement onTypeFormatting = capabilities.GetProperty(
                "documentOnTypeFormattingProvider");
            Assert.AreEqual(
                "}",
                onTypeFormatting.GetProperty("firstTriggerCharacter").GetString());
            JsonElement.ArrayEnumerator additionalFormattingTriggers = onTypeFormatting
                .GetProperty("moreTriggerCharacter")
                .EnumerateArray();
            Assert.IsTrue(additionalFormattingTriggers.MoveNext());
            Assert.AreEqual(";", additionalFormattingTriggers.Current.GetString());
            Assert.IsTrue(additionalFormattingTriggers.MoveNext());
            Assert.AreEqual("\n", additionalFormattingTriggers.Current.GetString());
            Assert.IsFalse(additionalFormattingTriggers.MoveNext());
            Assert.Contains(
                "source.organizeImports",
                capabilities
                    .GetProperty("codeActionProvider")
                    .GetProperty("codeActionKinds")
                    .EnumerateArray()
                    .Select(static value => value.GetString()));
            await lsp.OpenDocumentAsync(programPath, ProgramText).ConfigureAwait(false);

            var calculatorPosition = new Position(5, 22);
            PrepareRenameResult? prepared = await lsp.PrepareRenameAsync(
                programPath,
                calculatorPosition,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(prepared);
            Assert.AreEqual("Calculator", prepared.Placeholder);
            Assert.AreEqual(new Position(5, 20), prepared.Range.Start);
            Assert.AreEqual(new Position(5, 30), prepared.Range.End);

            WorkspaceEdit rename = await lsp.RequestRenameAsync(
                programPath,
                calculatorPosition,
                "Arithmetic",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, rename.DocumentChanges);
            TextDocumentEdit programRename = rename.DocumentChanges.Single(edit =>
                edit.TextDocument.Uri == DocumentUri.FromFileSystemPath(programPath));
            TextDocumentEdit consumerRename = rename.DocumentChanges.Single(edit =>
                edit.TextDocument.Uri == DocumentUri.FromFileSystemPath(consumerPath));
            Assert.AreEqual(1, programRename.TextDocument.Version);
            Assert.IsNull(consumerRename.TextDocument.Version);
            Assert.HasCount(2, programRename.Edits);
            Assert.HasCount(1, consumerRename.Edits);
            Assert.IsTrue(rename.DocumentChanges
                .SelectMany(static edit => edit.Edits)
                .All(static edit => edit.NewText == "Arithmetic"));

            RemoteInvocationException invalidRename =
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                    async () => await lsp.RequestRenameAsync(
                        programPath,
                        calculatorPosition,
                        "not valid",
                        TestContext.CancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
            Assert.Contains("valid C# identifier", invalidRename.Message, StringComparison.Ordinal);

            var formattingOptions = new FormattingOptions
            {
                TabSize = 4,
                InsertSpaces = true,
                TrimTrailingWhitespace = true,
                InsertFinalNewline = true,
                TrimFinalNewlines = true
            };
            IReadOnlyList<TextEdit> rangeFormatting = await lsp.RequestRangeFormattingAsync(
                programPath,
                new LspRange(new Position(5, 0), new Position(6, 0)),
                formattingOptions,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotEmpty(rangeFormatting);
            string rangeFormattedText = ApplyTextEdits(ProgramText, rangeFormatting);
            Assert.Contains(
                "public static class Calculator { public static int Add(int left, int right) => left + right; }",
                rangeFormattedText,
                StringComparison.Ordinal);
            Assert.Contains(
                "public static class Program{public static void Main(){Console.WriteLine(Calculator.Add(1,2));}}",
                rangeFormattedText,
                StringComparison.Ordinal);

            string calculatorLine = ProgramText.Split('\n')[5];
            int semicolonPosition = calculatorLine.IndexOf(';', StringComparison.Ordinal) + 1;
            (string Character, Position Position)[] onTypeRequests =
            [
                (";", new Position(5, semicolonPosition)),
                ("}", new Position(5, calculatorLine.Length)),
                ("\n", new Position(6, 0))
            ];
            foreach ((string character, Position position) in onTypeRequests)
            {
                IReadOnlyList<TextEdit> onTypeFormattingEdits = await lsp
                    .RequestOnTypeFormattingAsync(
                        programPath,
                        position,
                        character,
                        formattingOptions,
                        TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.IsNotEmpty(onTypeFormattingEdits);
                string onTypeFormattedText = ApplyTextEdits(
                    ProgramText,
                    onTypeFormattingEdits);
                Assert.Contains(
                    "public static class Calculator { public static int Add(int left, int right) => left + right; }",
                    onTypeFormattedText,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "public static class Program{public static void Main(){Console.WriteLine(Calculator.Add(1,2));}}",
                    onTypeFormattedText,
                    StringComparison.Ordinal);
            }

            IReadOnlyList<TextEdit> unsupportedOnTypeFormatting = await lsp
                .RequestOnTypeFormattingAsync(
                    programPath,
                    new Position(5, 0),
                    "(",
                    formattingOptions,
                    TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsEmpty(unsupportedOnTypeFormatting);

            IReadOnlyList<TextEdit> formatting = await lsp.RequestFormattingAsync(
                programPath,
                formattingOptions,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotEmpty(formatting);
            string formattedText = ApplyTextEdits(ProgramText, formatting);
            Assert.Contains(
                "public static class Calculator { public static int Add(int left, int right) => left + right; }",
                formattedText,
                StringComparison.Ordinal);
            Assert.Contains("Calculator.Add(1, 2)", formattedText, StringComparison.Ordinal);
            Assert.EndsWith("\n", formattedText, StringComparison.Ordinal);

            IReadOnlyList<CodeAction> actions = await lsp.RequestCodeActionsAsync(
                programPath,
                new LspRange(new Position(0, 0), new Position(1, 13)),
                ["source"],
                TestContext.CancellationToken).ConfigureAwait(false);
            CodeAction organizeImports = Assert.ContainsSingle(actions);
            Assert.AreEqual("Organize Imports", organizeImports.Title);
            Assert.AreEqual("source.organizeImports", organizeImports.Kind);
            Assert.IsNotNull(organizeImports.Edit);
            TextDocumentEdit organizeProgram = Assert.ContainsSingle(
                organizeImports.Edit.DocumentChanges);
            Assert.AreEqual(1, organizeProgram.TextDocument.Version);
            string organizedText = ApplyTextEdits(ProgramText, organizeProgram.Edits);
            Assert.StartsWith(
                "using System;\nusing System.Text;",
                organizedText,
                StringComparison.Ordinal);
            IReadOnlyList<CodeAction> quickFixOnly = await lsp.RequestCodeActionsAsync(
                programPath,
                new LspRange(new Position(0, 0), new Position(0, 0)),
                ["quickfix"],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(quickFixOnly);

            string overlayText = ProgramText.Replace(
                "Calculator",
                "TransientCalculator",
                StringComparison.Ordinal);
            await lsp.ChangeDocumentAsync(
                programPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = overlayText }]).ConfigureAwait(false);
            IReadOnlyList<DocumentSymbol> overlaySymbols = await lsp.RequestDocumentSymbolsAsync(
                programPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "TransientCalculator",
                FlattenNames(overlaySymbols));
            await lsp.CloseDocumentAsync(programPath).ConfigureAwait(false);
            IReadOnlyList<DocumentSymbol> persistedSymbols = await lsp.RequestDocumentSymbolsAsync(
                programPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("Calculator", FlattenNames(persistedSymbols));
            Assert.DoesNotContain("TransientCalculator", FlattenNames(persistedSymbols));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static List<string> FlattenNames(IReadOnlyList<DocumentSymbol> symbols)
    {
        var names = new List<string>();
        AddNames(symbols, names);
        return names;
    }

    private static void AddNames(
        IReadOnlyList<DocumentSymbol> symbols,
        List<string> names)
    {
        foreach (DocumentSymbol symbol in symbols)
        {
            names.Add(symbol.Name);
            if (symbol.Children is { Count: > 0 } children)
            {
                AddNames(children, names);
            }
        }
    }

    private static string ApplyTextEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        (int Start, int End, string NewText)[] replacements =
        [
            .. edits
                .Select(edit => (
                    Start: GetOffset(text, edit.Range.Start),
                    End: GetOffset(text, edit.Range.End),
                    edit.NewText))
                .OrderByDescending(static edit => edit.Start)
        ];
        var builder = new StringBuilder(text);
        foreach ((int start, int end, string newText) in replacements)
        {
            builder.Remove(start, end - start);
            builder.Insert(start, newText);
        }

        return builder.ToString();
    }

    private static int GetOffset(string text, Position position)
    {
        int line = 0;
        int offset = 0;
        while (line < position.Line && offset < text.Length)
        {
            if (text[offset++] == '\n')
            {
                line++;
            }
        }

        Assert.AreEqual(position.Line, line);
        int result = offset + position.Character;
        Assert.IsLessThanOrEqualTo(text.Length, result);
        return result;
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string ProgramText = """
        using System.Text;
        using System;

        namespace Fixture;

        public static class Calculator{public static int Add(int left,int right)=>left+right;}

        public static class Program{public static void Main(){Console.WriteLine(Calculator.Add(1,2));}}
        """;

    private const string ConsumerText = """
        namespace Fixture;

        public static class Consumer
        {
            public static int Value => Calculator.Add(2, 3);
        }
        """;
}

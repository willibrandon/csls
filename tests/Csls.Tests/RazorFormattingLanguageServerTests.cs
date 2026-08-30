using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies Razor formatting through a real language-server worker process.
/// </summary>
[TestClass]
public sealed class RazorFormattingLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Formats current component and view snapshots without changing raw text content.
    /// </summary>
    /// <param name="documentRelativePath">The Razor document path within the fixture.</param>
    /// <param name="importsRelativePath">The matching Razor imports path within the fixture.</param>
    /// <param name="membersDirective">The directive that declares generated class members.</param>
    /// <param name="useTabs">Whether the editor requested tab indentation.</param>
    [TestMethod]
    [DataRow("Component.razor", "_Imports.razor", "code", false)]
    [DataRow("Pages/Index.cshtml", "Pages/_ViewImports.cshtml", "functions", true)]
    public async Task RazorFormattingTracksCurrentProjectSnapshot(
        string documentRelativePath,
        string importsRelativePath,
        string membersDirective,
        bool useTabs)
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
            $"csls-razor-formatting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, documentRelativePath);
            string importsPath = Path.Join(fixturePath, importsRelativePath);
            Directory.CreateDirectory(
                Path.GetDirectoryName(documentPath)
                    ?? throw new InvalidOperationException("The Razor fixture has no directory."));
            string newline = useTabs ? "\r\n" : "\n";
            string persistedText = CreateText(UnformattedText, membersDirective, newline);
            string expectedText = CreateText(
                useTabs ? ExpectedTabs : ExpectedSpaces,
                membersDirective,
                newline);
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
                persistedText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-razor-formatting-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, persistedText, "razor")
                .ConfigureAwait(false);
            using var saveConfiguration = JsonDocument.Parse(
                """{"csls":{"formatOnSave":true}}""");
            await lsp.ChangeConfigurationAsync(saveConfiguration.RootElement)
                .ConfigureAwait(false);
            IReadOnlyList<TextEdit> saveEdits = await lsp.RequestSaveFormattingAsync(
                documentPath,
                TextDocumentSaveReason.Manual,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotEmpty(saveEdits);
            string expectedSaveText = CreateText(
                ExpectedSpaces,
                membersDirective,
                newline).TrimEnd('\r', '\n');
            Assert.AreEqual(expectedSaveText, ApplyTextEdits(persistedText, saveEdits));

            var options = new FormattingOptions
            {
                TabSize = 4,
                InsertSpaces = !useTabs,
                TrimTrailingWhitespace = true,
                InsertFinalNewline = true,
                TrimFinalNewlines = true
            };
            IReadOnlyList<TextEdit> rangeEdits = await lsp.RequestRangeFormattingAsync(
                documentPath,
                new LspRange(new Position(1, 0), new Position(2, 0)),
                options,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotEmpty(rangeEdits);
            string expectedRangeText = persistedText.Replace(
                "<p>@(1+2)</p>",
                useTabs ? "\t<p>@(1 + 2)</p>" : "    <p>@(1 + 2)</p>",
                StringComparison.Ordinal);
            Assert.AreEqual(expectedRangeText, ApplyTextEdits(persistedText, rangeEdits));

            string memberLine = persistedText
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')[12];
            IReadOnlyList<TextEdit> onTypeEdits = await lsp.RequestOnTypeFormattingAsync(
                documentPath,
                new Position(12, memberLine.Length),
                ";",
                options,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotEmpty(onTypeEdits);
            string expectedOnTypeText = persistedText.Replace(
                "private int count=0;",
                useTabs ? "\tprivate int count = 0;" : "    private int count = 0;",
                StringComparison.Ordinal);
            Assert.AreEqual(expectedOnTypeText, ApplyTextEdits(persistedText, onTypeEdits));

            IReadOnlyList<TextEdit> edits = await lsp.RequestFormattingAsync(
                documentPath,
                options,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotEmpty(edits);
            Assert.AreEqual(expectedText, ApplyTextEdits(persistedText, edits));

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = expectedText }])
                .ConfigureAwait(false);
            IReadOnlyList<TextEdit> repeated = await lsp.RequestFormattingAsync(
                documentPath,
                options,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(repeated);

            ControlSessionInfo session = await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(session.SocketPath);
            await using ConfiguredAsyncDisposable controlCleanup = control.ConfigureAwait(false);
            ControlWorkspaceOperationResult reload = await control.ReloadWorkspaceAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(reload.PreviousGeneration + 1, reload.CurrentGeneration);
            IReadOnlyList<TextEdit> reloaded = await lsp.RequestFormattingAsync(
                documentPath,
                options,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(reloaded);

            if (!useTabs)
            {
                await lsp.ChangeDocumentAsync(
                    documentPath,
                    version: 3,
                    [new TextDocumentContentChangeEvent { Text = NestedControlFlowText }])
                    .ConfigureAwait(false);
                IReadOnlyList<TextEdit> nestedEdits = await lsp.RequestFormattingAsync(
                    documentPath,
                    options,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(
                    ExpectedNestedControlFlow,
                    ApplyTextEdits(NestedControlFlowText, nestedEdits));
            }

            await lsp.CloseDocumentAsync(documentPath).ConfigureAwait(false);
            IReadOnlyList<TextEdit> restored = await lsp.RequestFormattingAsync(
                documentPath,
                options,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expectedText, ApplyTextEdits(persistedText, restored));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
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

    private static string CreateText(string value, string membersDirective, string newline) =>
        value
            .Replace("@members", $"@{membersDirective}", StringComparison.Ordinal)
            .Replace("\n", newline, StringComparison.Ordinal);

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    private const string UnformattedText = """
        <div>
        <p>@(1+2)</p>
        @if(true)
        {
        <span>Value</span>
        }
        <textarea name="value"
        id="value">
          literal
            </textarea>
        </div>
        @members{
        private int count=0;
        private void Increment()
        {
        count++;
        }
        }
        """;

    private const string ExpectedSpaces = """
        <div>
            <p>@(1 + 2)</p>
            @if (true)
            {
                <span>Value</span>
            }
            <textarea name="value"
                      id="value">
          literal
            </textarea>
        </div>
        @members {
            private int count = 0;
            private void Increment()
            {
                count++;
            }
        }
        """ + "\n";

    private const string ExpectedTabs =
        "<div>\n" +
        "\t<p>@(1 + 2)</p>\n" +
        "\t@if (true)\n" +
        "\t{\n" +
        "\t\t<span>Value</span>\n" +
        "\t}\n" +
        "\t<textarea name=\"value\"\n" +
        "\t\t\t  id=\"value\">\n" +
        "  literal\n" +
        "    </textarea>\n" +
        "</div>\n" +
        "@members {\n" +
        "\tprivate int count = 0;\n" +
        "\tprivate void Increment()\n" +
        "\t{\n" +
        "\t\tcount++;\n" +
        "\t}\n" +
        "}\n";

    private const string NestedControlFlowText = """
        <ol>
        @for(int i=0;i<2;i++)
        {
        <li>
        @switch(i)
        {
        case 0:
        <text>first</text>
        break;
        default:
        <text>next</text>
        break;
        }
        </li>
        }
        </ol>
        <p>
        @*
        comment body
        *@
        </p>
        """;

    private const string ExpectedNestedControlFlow = """
        <ol>
            @for (int i = 0; i < 2; i++)
            {
                <li>
                    @switch (i)
                    {
                        case 0:
                            <text>first</text>
                            break;
                        default:
                            <text>next</text>
                            break;
                    }
                </li>
            }
        </ol>
        <p>
            @*
        comment body
        *@
        </p>
        """ + "\n";
}

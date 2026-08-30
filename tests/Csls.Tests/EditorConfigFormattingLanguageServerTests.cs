using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Tests;

/// <summary>
/// Verifies that real LSP formatting composes document and editor preferences.
/// </summary>
[TestClass]
public sealed class EditorConfigFormattingLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Preserves per-file C# options while using the editor's requested indentation.
    /// </summary>
    [TestMethod]
    public async Task DocumentFormattingPreservesEditorConfigAndUsesEditorIndentation()
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
            $"csls-editorconfig-formatting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, ".editorconfig"),
                EditorConfigText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-editorconfig-formatting-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            var editorOptions = new FormattingOptions
            {
                InsertSpaces = true,
                TabSize = 2
            };
            IReadOnlyList<TextEdit> edits = await lsp.RequestFormattingAsync(
                documentPath,
                editorOptions,
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNotEmpty(edits);
            Assert.AreEqual(ExpectedText, ApplyTextEdits(DocumentText, edits));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
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

    private const string EditorConfigText = """
        root = true

        [*.cs]
        indent_style = tab
        indent_size = 8
        tab_width = 8
        end_of_line = lf
        csharp_new_line_before_open_brace = none
        """;

    private const string DocumentText = """
        namespace Fixture
        {
        public static class Program
        {
        public static void Main()
        {
        if(true)
        {
        Console.WriteLine("ready");
        }
        }
        }
        }
        """;

    private const string ExpectedText = """
        namespace Fixture {
          public static class Program {
            public static void Main() {
              if (true) {
                Console.WriteLine("ready");
              }
            }
          }
        }
        """;
}

using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies C# documentation rendering through a real language-server worker.
/// </summary>
[TestClass]
public sealed class DocumentationLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Preserves structured XML documentation across hover, completion, and signature help.
    /// </summary>
    [TestMethod]
    public async Task StructuredDocumentationFlowsThroughLanguageFeatures()
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
            $"csls-documentation-{Guid.NewGuid():N}");
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

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-documentation-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "textDocument": {
                    "diagnostic": {},
                    "hover": {
                      "contentFormat": ["markdown", "plaintext"]
                    },
                    "completion": {
                      "completionItem": {
                        "documentationFormat": ["markdown", "plaintext"],
                        "resolveSupport": {
                          "properties": ["documentation"]
                        }
                      }
                    },
                    "signatureHelp": {
                      "signatureInformation": {
                        "documentationFormat": ["markdown", "plaintext"]
                      }
                    }
                  }
                }
                """);
            await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            JsonElement hoverElement = await lsp.RequestHoverAsync(
                documentPath,
                GetPosition(DocumentText, "Describe(new Widget"),
                TestContext.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The documented method returned no hover.");
            Hover hover = hoverElement.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException("The documented method returned invalid hover.");
            Assert.AreEqual("markdown", hover.Contents.Kind);
            Assert.Contains("Describes a", hover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("Widget", hover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("fast path", hover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("fallback path", hover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("• Use the fast path", hover.Contents.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("<see", hover.Contents.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("<list", hover.Contents.Value, StringComparison.Ordinal);

            CompletionList completion = await lsp.RequestCompletionAsync(
                documentPath,
                GetPosition(DocumentText, "Catalog.Des\n", "Catalog.Des".Length),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem describe = completion.Items.Single(
                static item => item.Label == "Describe");
            CompletionItem resolved = await lsp.ResolveCompletionAsync(
                describe,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolved.Documentation);
            Assert.AreEqual("markdown", resolved.Documentation.Kind);
            Assert.Contains("Describes a", resolved.Documentation.Value, StringComparison.Ordinal);
            Assert.Contains("Widget", resolved.Documentation.Value, StringComparison.Ordinal);
            Assert.Contains("fast path", resolved.Documentation.Value, StringComparison.Ordinal);
            Assert.Contains(
                "• Use the fast path",
                resolved.Documentation.Value,
                StringComparison.Ordinal);
            Assert.Contains(
                "Widget guide",
                resolved.Documentation.Value,
                StringComparison.Ordinal);
            Assert.Contains(
                "[Widget guide](https://example.com/widget)",
                resolved.Documentation.Value,
                StringComparison.Ordinal);
            Assert.DoesNotContain("<see", resolved.Documentation.Value, StringComparison.Ordinal);

            CompletionList supplementalCompletion = await lsp.RequestCompletionAsync(
                documentPath,
                GetPosition(DocumentText, "Catalog.Gui\n", "Catalog.Gui".Length),
                TestContext.CancellationToken).ConfigureAwait(false);
            CompletionItem guideOnly = supplementalCompletion.Items.Single(
                static item => item.Label == "GuideOnly");
            CompletionItem resolvedGuideOnly = await lsp.ResolveCompletionAsync(
                guideOnly,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(resolvedGuideOnly.Documentation);
            Assert.Contains(
                "Standalone guide",
                resolvedGuideOnly.Documentation.Value,
                StringComparison.Ordinal);

            SignatureHelp? signatureHelp = await lsp.RequestSignatureHelpAsync(
                documentPath,
                GetPosition(
                    DocumentText,
                    "Catalog.Describe(new Widget",
                    "Catalog.Describe(".Length),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(signatureHelp);
            SignatureInformation signature = Assert.ContainsSingle(signatureHelp.Signatures);
            Assert.IsNotNull(signature.Documentation);
            Assert.AreEqual("markdown", signature.Documentation.Kind);
            Assert.Contains(
                "Describes a",
                signature.Documentation.Value,
                StringComparison.Ordinal);
            Assert.Contains("Widget", signature.Documentation.Value, StringComparison.Ordinal);
            Assert.Contains(
                "Widget guide",
                signature.Documentation.Value,
                StringComparison.Ordinal);
            Assert.IsNotNull(signature.Parameters);
            ParameterInformation parameter = Assert.ContainsSingle(signature.Parameters);
            Assert.IsNotNull(parameter.Documentation);
            Assert.AreEqual("markdown", parameter.Documentation.Kind);
            Assert.Contains(
                "Widget to render",
                parameter.Documentation.Value,
                StringComparison.Ordinal);

            SignatureHelp? inheritedHelp = await lsp.RequestSignatureHelpAsync(
                documentPath,
                GetPosition(
                    DocumentText,
                    "renderer.Render(new Widget",
                    "renderer.Render(".Length),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(inheritedHelp);
            SignatureInformation inheritedSignature = Assert.ContainsSingle(
                inheritedHelp.Signatures);
            Assert.IsNotNull(inheritedSignature.Documentation);
            Assert.Contains(
                "Renders a widget",
                inheritedSignature.Documentation.Value,
                StringComparison.Ordinal);
            Assert.IsNotNull(inheritedSignature.Parameters);
            ParameterInformation inheritedParameter = Assert.ContainsSingle(
                inheritedSignature.Parameters);
            Assert.IsNotNull(inheritedParameter.Documentation);
            Assert.Contains(
                "Widget supplied by the caller",
                inheritedParameter.Documentation.Value,
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

    private static Position GetPosition(string source, string marker, int relativeOffset = 0)
    {
        int offset = source.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, offset, $"Marker '{marker}' was not found.");
        offset += relativeOffset;
        int line = 0;
        int lineStart = 0;
        for (int index = 0; index < offset; index++)
        {
            if (source[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        return new Position(line, offset - lineStart);
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public sealed class Widget;

        public interface IRenderer
        {
            /// <summary>
            /// Renders a widget.
            /// </summary>
            /// <param name="value">Widget supplied by the caller.</param>
            string Render(Widget value);
        }

        public sealed class Renderer : IRenderer
        {
            /// <inheritdoc />
            public string Render(Widget value) => value.ToString();
        }

        public static class Catalog
        {
            /// <summary>
            /// Describes a <see cref="Widget"/> instance.
            /// <para>Choose one of these paths:</para>
            /// <list type="bullet">
            /// <item><description>Use the fast path.</description></item>
            /// <item><description>Use the fallback path.</description></item>
            /// </list>
            /// </summary>
            /// <param name="value">Widget to render.</param>
            /// <returns>A description, or <see langword="null"/>.</returns>
            /// <seealso href="https://example.com/widget">Widget guide</seealso>
            public static string? Describe(Widget value) => value.ToString();

            /// <seealso href="https://example.com/standalone">Standalone guide</seealso>
            public static void GuideOnly()
            {
            }
        }

        public static class Program
        {
            public static void Main()
            {
                string? result = Catalog.Describe(new Widget());
                Renderer renderer = new();
                string inherited = renderer.Render(new Widget());
                Console.WriteLine(result);
                Console.WriteLine(inherited);
                Catalog.Des
                Catalog.Gui
            }
        }
        """;
}

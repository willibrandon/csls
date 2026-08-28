using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies project-context selection for real multi-targeted workspaces.
/// </summary>
[TestClass]
public sealed class MultiTargetWorkspaceLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Uses the best available target-framework flavor for position-based requests.
    /// </summary>
    [TestMethod]
    public async Task HoverUsesBestTargetFrameworkFlavor()
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
            $"csls-multi-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Target.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            var lsp = LspProcessSession.Start(
                "csls-multi-target-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            JsonElement hoverElement = await lsp.RequestHoverAsync(
                documentPath,
                GetPosition(DocumentText, "Value"),
                TestContext.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    "The best target-framework flavor returned no hover.");
            Hover hover = hoverElement.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException(
                    "The best target-framework flavor returned invalid hover.");
            Assert.Contains("int", hover.Contents.Value, StringComparison.Ordinal);
            Assert.Contains("Target.Value", hover.Contents.Value, StringComparison.Ordinal);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static Position GetPosition(string text, string value)
    {
        int offset = text.IndexOf(value, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, offset);
        int line = 0;
        int lineStart = 0;
        for (int index = 0; index < offset; index++)
        {
            if (text[index] == '\n')
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
            <TargetFrameworks>netstandard2.0;net10.0</TargetFrameworks>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public static class Target
        {
        #if NET10_0_OR_GREATER
            public static int Value => 10;
        #else
            public static string Value => "other";
        #endif
        }
        """;
}

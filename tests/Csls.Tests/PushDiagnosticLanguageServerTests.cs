using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies negotiated push diagnostics through a real language-server process.
/// </summary>
[TestClass]
public sealed class PushDiagnosticLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Publishes versioned diagnostics, coalesces edits, refreshes saves, and clears closes.
    /// </summary>
    [TestMethod]
    public async Task LegacyClientReceivesCurrentDocumentDiagnostics()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = CreateFixturePath();
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = await WriteFixtureAsync(fixturePath).ConfigureAwait(false);
            var client = new LspTestClient(
                legacyConfiguration: null,
                preferredConfiguration: null);
            var lsp = LspProcessSession.Start(
                "csls-push-diagnostic-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse("{}");
            await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);

            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);
            PublishDiagnosticsParams opened = await client.ReadPublishedDiagnosticsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(documentPath), opened.Uri);
            Assert.AreEqual(1, opened.Version);
            Assert.Contains("CS0103", opened.Diagnostics.Select(static item => item.Code));
            Assert.Contains("CA1822", opened.Diagnostics.Select(static item => item.Code));

            string intermediateText = DocumentText.Replace(
                "Missing",
                "StillMissing",
                StringComparison.Ordinal);
            string correctedText = DocumentText.Replace(
                "Missing",
                "\"hello\"",
                StringComparison.Ordinal);
            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = intermediateText }])
                .ConfigureAwait(false);
            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 3,
                [new TextDocumentContentChangeEvent { Text = correctedText }])
                .ConfigureAwait(false);

            PublishDiagnosticsParams changed = await client.ReadPublishedDiagnosticsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(3, changed.Version);
            Assert.DoesNotContain("CS0103", changed.Diagnostics.Select(static item => item.Code));
            Assert.Contains("CA1822", changed.Diagnostics.Select(static item => item.Code));

            await lsp.SaveDocumentAsync(documentPath).ConfigureAwait(false);
            PublishDiagnosticsParams saved = await client.ReadPublishedDiagnosticsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(3, saved.Version);
            Assert.AreSequenceEqual(
                changed.Diagnostics.Select(static item => item.Code).ToArray(),
                saved.Diagnostics.Select(static item => item.Code).ToArray());

            await lsp.CloseDocumentAsync(documentPath).ConfigureAwait(false);
            PublishDiagnosticsParams closed = await client.ReadPublishedDiagnosticsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNull(closed.Version);
            Assert.IsEmpty(closed.Diagnostics);

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
    /// Suppresses legacy notifications when the client advertises pull diagnostics.
    /// </summary>
    [TestMethod]
    public async Task PullCapableClientDoesNotReceivePushDiagnostics()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = CreateFixturePath();
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = await WriteFixtureAsync(fixturePath).ConfigureAwait(false);
            var client = new LspTestClient(
                legacyConfiguration: null,
                preferredConfiguration: null);
            var lsp = LspProcessSession.Start(
                "csls-pull-diagnostic-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "textDocument": {
                    "diagnostic": {}
                  }
                }
                """);
            await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            DocumentDiagnosticReport report = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "CS0103",
                report.Items?.Select(static item => item.Code) ?? []);
            Assert.IsFalse(client.TryReadPublishedDiagnostics(out _));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static string CreateFixturePath() => Path.Join(
        Path.GetTempPath(),
        $"csls-push-diagnostics-{Guid.NewGuid():N}");

    private static string ResolveWorkerPath(string repositoryRoot)
    {
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        return workerPath;
    }

    private async Task<string> WriteFixtureAsync(string fixturePath)
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
        return documentPath;
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <EnableNETAnalyzers>true</EnableNETAnalyzers>
            <AnalysisLevel>latest</AnalysisLevel>
            <AnalysisMode>AllEnabledByDefault</AnalysisMode>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        public sealed class Program
        {
            public int GetValue() => 42;

            public static void Main()
            {
                Console.WriteLine(Missing);
            }
        }
        """;
}

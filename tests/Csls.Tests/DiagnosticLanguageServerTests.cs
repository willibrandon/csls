using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies pull diagnostics, analyzer execution, caching, and incremental synchronization.
/// </summary>
[TestClass]
public sealed class DiagnosticLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Invalidates snapshot diagnostics after a real incremental edit and preserves analyzer findings.
    /// </summary>
    [TestMethod]
    public async Task PullDiagnosticsTrackIncrementalDocumentGeneration()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-diagnostics-{Guid.NewGuid():N}");
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
                "csls-diagnostic-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement diagnosticProvider = initialization
                .GetProperty("capabilities")
                .GetProperty("diagnosticProvider");
            Assert.AreEqual("csls", diagnosticProvider.GetProperty("identifier").GetString());
            Assert.IsTrue(diagnosticProvider.GetProperty("interFileDependencies").GetBoolean());
            Assert.IsFalse(diagnosticProvider.GetProperty("workspaceDiagnostics").GetBoolean());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            DocumentDiagnosticReport initial = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", initial.Kind);
            Assert.IsNotNull(initial.ResultId);
            IReadOnlyList<Diagnostic> initialItems = initial.Items
                ?? throw new InvalidDataException("A full diagnostic report had no items.");
            Assert.Contains("CS0103", initialItems.Select(static diagnostic => diagnostic.Code));
            Assert.Contains("CA1822", initialItems.Select(static diagnostic => diagnostic.Code));

            DocumentDiagnosticReport unchanged = await lsp.RequestDiagnosticsAsync(
                documentPath,
                initial.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("unchanged", unchanged.Kind);
            Assert.AreEqual(initial.ResultId, unchanged.ResultId);
            Assert.IsNull(unchanged.Items);

            await lsp.ChangeDocumentAsync(
                documentPath,
                version: 2,
                [
                    new TextDocumentContentChangeEvent
                    {
                        Range = new LspRange(
                            new Position(8, 26),
                            new Position(8, 33)),
                        RangeLength = 7,
                        Text = "\"hello\""
                    }
                ]).ConfigureAwait(false);
            await lsp.SaveDocumentAsync(documentPath).ConfigureAwait(false);

            DocumentDiagnosticReport updated = await lsp.RequestDiagnosticsAsync(
                documentPath,
                initial.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", updated.Kind);
            Assert.AreNotEqual(initial.ResultId, updated.ResultId);
            IReadOnlyList<Diagnostic> updatedItems = updated.Items
                ?? throw new InvalidDataException("An updated diagnostic report had no items.");
            Assert.DoesNotContain("CS0103", updatedItems.Select(static diagnostic => diagnostic.Code));
            Assert.Contains("CA1822", updatedItems.Select(static diagnostic => diagnostic.Code));

            DocumentDiagnosticReport updatedUnchanged = await lsp.RequestDiagnosticsAsync(
                documentPath,
                updated.ResultId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("unchanged", updatedUnchanged.Kind);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
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

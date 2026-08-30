using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies multi-solution discovery through a real out-of-process language server.
/// </summary>
[TestClass]
public sealed class MultiSolutionLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Serves C# and Razor documents from every nested solution while ignoring decoys.
    /// </summary>
    [TestMethod]
    public async Task WorkerServesEveryNestedSolution()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-multiple-solutions-lsp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string alphaDirectory = Path.Join(workspacePath, "alpha");
            string betaDirectory = Path.Join(workspacePath, "beta");
            string excludedDirectory = Path.Join(workspacePath, ".direnv");
            Directory.CreateDirectory(alphaDirectory);
            Directory.CreateDirectory(betaDirectory);
            Directory.CreateDirectory(excludedDirectory);
            (string alphaDocument, string alphaRazorDocument) = await WriteSolutionAsync(
                alphaDirectory,
                "Alpha",
                TestContext.CancellationToken).ConfigureAwait(false);
            (string betaDocument, string betaRazorDocument) = await WriteSolutionAsync(
                betaDirectory,
                "Beta",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(excludedDirectory, "Decoy.slnx"),
                "<invalid>",
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-multiple-solutions",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                workspacePath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                workspacePath,
                TestContext.CancellationToken).ConfigureAwait(false);

            foreach ((string documentPath, string razorDocumentPath) in new[]
            {
                (alphaDocument, alphaRazorDocument),
                (betaDocument, betaRazorDocument)
            })
            {
                string documentText = await File.ReadAllTextAsync(
                    documentPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await lsp.OpenDocumentAsync(documentPath, documentText).ConfigureAwait(false);
                JsonElement hoverElement = await lsp.RequestHoverAsync(
                    documentPath,
                    new Position(9, 10),
                    TestContext.CancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The nested solution returned no hover.");
                Hover hover = hoverElement.Deserialize(
                    LspJsonSerializerContext.Default.Hover)
                    ?? throw new InvalidDataException("The nested solution returned invalid hover.");
                Assert.Contains("System.Console", hover.Contents.Value);

                string razorDocumentText = await File.ReadAllTextAsync(
                    razorDocumentPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await lsp.OpenDocumentAsync(
                    razorDocumentPath,
                    razorDocumentText,
                    "razor").ConfigureAwait(false);
                DocumentDiagnosticReport report = await lsp.RequestDiagnosticsAsync(
                    razorDocumentPath,
                    previousResultId: null,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("full", report.Kind);
                Diagnostic diagnostic = report.Items?
                    .Single(static item => item.Code == "CS0103")
                    ?? throw new InvalidDataException(
                        "The nested Razor project returned no mapped compiler diagnostic.");
                Assert.AreEqual("C#", diagnostic.Source);
                Assert.AreEqual(new Position(0, 4), diagnostic.Range.Start);
                Assert.AreEqual(new Position(0, 15), diagnostic.Range.End);
            }

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static async Task<(string DocumentPath, string RazorDocumentPath)> WriteSolutionAsync(
        string directoryPath,
        string projectName,
        CancellationToken cancellationToken)
    {
        string projectPath = Path.Join(directoryPath, $"{projectName}.csproj");
        string solutionPath = Path.Join(directoryPath, $"{projectName}.slnx");
        string documentPath = Path.Join(directoryPath, "Program.cs");
        string razorDocumentPath = Path.Join(directoryPath, "Component.razor");
        await File.WriteAllTextAsync(
            projectPath,
            ProjectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            solutionPath,
            $"<Solution><Project Path=\"{projectName}.csproj\" /></Solution>",
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            DocumentText.Replace("Fixture", projectName, StringComparison.Ordinal),
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            razorDocumentPath,
            "<p>@MissingName</p>",
            cancellationToken).ConfigureAwait(false);
        return (documentPath, razorDocumentPath);
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        namespace Fixture;

        /// <summary>
        /// Supplies the executable entry point for one solution fixture.
        /// </summary>
        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("hello");
            }
        }
        """;
}

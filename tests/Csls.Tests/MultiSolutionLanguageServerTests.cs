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
    /// Serves documents from every nested solution while ignoring generated-directory decoys.
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
            string alphaDocument = await WriteSolutionAsync(
                alphaDirectory,
                "Alpha",
                TestContext.CancellationToken).ConfigureAwait(false);
            string betaDocument = await WriteSolutionAsync(
                betaDirectory,
                "Beta",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(excludedDirectory, "Decoy.slnx"),
                "<invalid>",
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-multiple-solutions",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                workspacePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                workspacePath,
                TestContext.CancellationToken).ConfigureAwait(false);

            foreach (string documentPath in new[] { alphaDocument, betaDocument })
            {
                string documentText = await File.ReadAllTextAsync(
                    documentPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                await lsp.OpenDocumentAsync(documentPath, documentText).ConfigureAwait(false);
                JsonElement hoverElement = await lsp.RequestHoverAsync(
                    documentPath,
                    new Position(6, 10),
                    TestContext.CancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The nested solution returned no hover.");
                Hover hover = hoverElement.Deserialize(
                    LspJsonSerializerContext.Default.Hover)
                    ?? throw new InvalidDataException("The nested solution returned invalid hover.");
                Assert.Contains("System.Console", hover.Contents.Value);
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

    private static async Task<string> WriteSolutionAsync(
        string directoryPath,
        string projectName,
        CancellationToken cancellationToken)
    {
        string projectPath = Path.Join(directoryPath, $"{projectName}.csproj");
        string solutionPath = Path.Join(directoryPath, $"{projectName}.slnx");
        string documentPath = Path.Join(directoryPath, "Program.cs");
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
        return documentPath;
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

        public static class Program
        {
            public static void Main()
            {
                Console.WriteLine("hello");
            }
        }
        """;
}

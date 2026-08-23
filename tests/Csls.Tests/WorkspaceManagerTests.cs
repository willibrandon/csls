using Csls.Protocol;
using Csls.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies real Roslyn workspace behavior over temporary source trees.
/// </summary>
[TestClass]
public sealed class WorkspaceManagerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Resolves framework symbols from a loose C# file without a project.
    /// </summary>
    [TestMethod]
    public async Task LooseFileResolvesFrameworkSymbolHover()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-loose-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string documentPath = Path.Join(workspacePath, "Program.cs");
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var manager = new WorkspaceManager(
                NullLogger<WorkspaceManager>.Instance);
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [documentPath],
                TestContext.CancellationToken).ConfigureAwait(false);
            var documentUri = DocumentUri.FromFileSystemPath(documentPath);
            await manager.OpenDocumentAsync(
                new TextDocumentItem
                {
                    Uri = documentUri,
                    LanguageId = "csharp",
                    Version = 1,
                    Text = DocumentText
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Hover? hover = await manager.GetHoverAsync(
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = documentUri },
                    Position = new Position(6, 9)
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsNotNull(hover);
            Assert.Contains("System.Console", hover.Contents.Value);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Loads every nested solution while excluding generated workspace directories.
    /// </summary>
    [TestMethod]
    public async Task MultipleNestedSolutionsAreLoadedAndExcludedDirectoriesAreIgnored()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-multiple-solutions-{Guid.NewGuid():N}");
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

            var manager = new WorkspaceManager(NullLogger<WorkspaceManager>.Instance);
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(workspacePath, Assert.ContainsSingle(manager.WorkspaceRoots));

            foreach (string documentPath in new[] { alphaDocument, betaDocument })
            {
                string documentText = await File.ReadAllTextAsync(
                    documentPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                var documentUri = DocumentUri.FromFileSystemPath(documentPath);
                await manager.OpenDocumentAsync(
                    new TextDocumentItem
                    {
                        Uri = documentUri,
                        LanguageId = "csharp",
                        Version = 1,
                        Text = documentText
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Hover? hover = await manager.GetHoverAsync(
                    new TextDocumentPositionParams
                    {
                        TextDocument = new TextDocumentIdentifier { Uri = documentUri },
                        Position = new Position(6, 10)
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNotNull(hover);
                Assert.Contains("System.Console", hover.Contents.Value);
            }
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

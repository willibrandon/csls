using Csls.Protocol;
using Csls.Workspaces;
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
    /// Uses the synchronized SDK project identity when a loose workspace has one project file.
    /// </summary>
    [TestMethod]
    public async Task LooseWorkspaceUsesSingleProjectIdentity()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-loose-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string projectPath = Path.Join(workspacePath, "Fixture.csproj");
            string documentPath = Path.Join(workspacePath, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                "public sealed class Program;",
                TestContext.CancellationToken).ConfigureAwait(false);

            string trustedPlatformAssemblies = AppContext.GetData(
                "TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException(
                    "The test host did not provide trusted platform assemblies.");
            var loader = new LooseFileWorkspaceLoader(trustedPlatformAssemblies.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries));
            WorkspaceFolderSnapshot snapshot = Assert.ContainsSingle(await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false));
            using (snapshot.Workspace)
            {
                Microsoft.CodeAnalysis.Project project = Assert.ContainsSingle(
                    snapshot.Solution.Projects);
                Assert.AreEqual("Fixture", project.Name);
                Assert.AreEqual(projectPath, project.FilePath);
                Assert.AreEqual(
                    documentPath,
                    Assert.ContainsSingle(project.Documents.Where(
                        static document => document.FilePath is not null)).FilePath);
            }
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Resolves framework symbols from a real SDK-backed file-based app.
    /// </summary>
    [TestMethod]
    public async Task FileBasedAppResolvesFrameworkSymbolHover()
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

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
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
    /// Serializes concurrent SDK evaluation of the same physical file-based app.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentFileBasedAppLoadsDoNotCollide()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-app-concurrent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string documentPath = Path.Join(workspacePath, "Program.cs");
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceManager first = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable firstDisposal = first.ConfigureAwait(false);
            WorkspaceManager second = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable secondDisposal = second.ConfigureAwait(false);
            await Task.WhenAll(
                first.LoadAsync([documentPath], TestContext.CancellationToken),
                second.LoadAsync([documentPath], TestContext.CancellationToken)).ConfigureAwait(false);

            Assert.AreEqual(1, first.Generation);
            Assert.AreEqual(1, second.Generation);
            Assert.IsFalse(File.Exists(documentPath + ".csproj"));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Loads one real solution once when clients repeat the same workspace root.
    /// </summary>
    [TestMethod]
    public async Task DuplicateWorkspaceRootsLoadOnce()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-duplicate-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string documentPath = await WriteSolutionAsync(
                workspacePath,
                "DuplicateWorkspace",
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);

            await manager.LoadAsync(
                [workspacePath, workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.AreEqual(workspacePath, Assert.ContainsSingle(manager.WorkspaceRoots));
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
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Loads an extensionless shebang app through the selected SDK.
    /// </summary>
    [TestMethod]
    public async Task ExtensionlessFileBasedAppResolvesFrameworkSymbolHover()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-app-extensionless-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string documentPath = Path.Join(workspacePath, "hello");
            await File.WriteAllTextAsync(
                documentPath,
                ExtensionlessDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
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
                    Text = ExtensionlessDocumentText
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            Hover? hover = await manager.GetHoverAsync(
                new TextDocumentPositionParams
                {
                    TextDocument = new TextDocumentIdentifier { Uri = documentUri },
                    Position = new Position(1, 1)
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(hover);
            Assert.Contains("System.Console", hover.Contents.Value);
            Assert.IsFalse(File.Exists(documentPath + ".csproj"));
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

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
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

    /// <summary>
    /// Inspects all or one nested project while retaining stable diagnostic order.
    /// </summary>
    [TestMethod]
    public async Task MultipleNestedProjectDiagnosticsSupportBoundedInspection()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-inspection-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string alphaDirectory = Path.Join(workspacePath, "alpha");
            string betaDirectory = Path.Join(workspacePath, "beta");
            Directory.CreateDirectory(alphaDirectory);
            Directory.CreateDirectory(betaDirectory);
            string alphaDocument = await WriteSolutionAsync(
                alphaDirectory,
                "Alpha",
                TestContext.CancellationToken).ConfigureAwait(false);
            string betaDocument = await WriteSolutionAsync(
                betaDirectory,
                "Beta",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                alphaDocument,
                DocumentText.Replace("\"hello\"", "MissingAlpha", StringComparison.Ordinal),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                betaDocument,
                DocumentText.Replace("\"hello\"", "MissingBeta", StringComparison.Ordinal),
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceInspectionSnapshot snapshot = await manager.InspectAsync(
                includeDiagnostics: true,
                diagnosticsProjectId: null,
                TestContext.CancellationToken).ConfigureAwait(false);

            Assert.IsTrue(snapshot.DiagnosticsLoaded);
            Assert.AreEqual(
                2,
                snapshot.TotalDiagnostics,
                string.Join(
                    Environment.NewLine,
                    snapshot.Diagnostics.Select(static diagnostic =>
                        $"{diagnostic.ProjectName}: {diagnostic.Id}: {diagnostic.Message}")));
            Assert.AreEqual(
                "Alpha,Beta",
                string.Join(',', snapshot.Diagnostics.Select(
                    static diagnostic => diagnostic.ProjectName)));
            Assert.IsTrue(snapshot.Diagnostics.All(
                static diagnostic => diagnostic.Id == "CS0103"));

            string alphaProjectId = Assert.ContainsSingle(snapshot.Projects
                .Where(static project => project.Name == "Alpha")).Id;
            WorkspaceInspectionSnapshot alphaSnapshot = await manager.InspectAsync(
                includeDiagnostics: true,
                alphaProjectId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(2, alphaSnapshot.Projects);
            Assert.AreEqual(1, alphaSnapshot.TotalDiagnostics);
            Assert.AreEqual(
                "Alpha",
                Assert.ContainsSingle(alphaSnapshot.Diagnostics).ProjectName);

            KeyNotFoundException missingProject =
                await Assert.ThrowsExactlyAsync<KeyNotFoundException>(() => manager.InspectAsync(
                    includeDiagnostics: true,
                    Guid.NewGuid().ToString("D"),
                    TestContext.CancellationToken)).ConfigureAwait(false);
            Assert.Contains("was not found", missingProject.Message, StringComparison.Ordinal);
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

    private const string ExtensionlessDocumentText = """
        #!/usr/bin/env dotnet
        Console.WriteLine("hello");
        """;
}

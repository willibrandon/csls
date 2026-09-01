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
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads synchronized solution projects and file-based apps without an MSBuild process.
    /// </summary>
    [TestMethod]
    public async Task SynchronizedWorkspaceLoadsSolutionAndFileBasedApp()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-synchronized-project-{Guid.NewGuid():N}");
        string projectDirectory = Path.Join(workspacePath, "App");
        string libraryDirectory = Path.Join(workspacePath, "Library");
        string toolsDirectory = Path.Join(workspacePath, "Tools");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(libraryDirectory);
        Directory.CreateDirectory(toolsDirectory);
        IReadOnlyList<WorkspaceFolderSnapshot> snapshots = [];
        try
        {
            string projectPath = Path.Join(projectDirectory, "Fixture.csproj");
            string documentPath = Path.Join(workspacePath, "Program.cs");
            string fileBasedAppPath = Path.Join(toolsDirectory, "Tool.cs");
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="../Program.cs" />
                    <ProjectReference Include="../Library/Library.csproj" />
                  </ItemGroup>
                </Project>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                """
                <Solution>
                  <Project Path="App/Fixture.csproj" />
                </Solution>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(libraryDirectory, "Library.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                "Console.WriteLine(Shared.Value);",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(libraryDirectory, "Shared.cs"),
                "public static class Shared { public const int Value = 1; }",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                fileBasedAppPath,
                """
                #:property TargetFramework=net10.0

                Console.WriteLine("tool");
                """,
                TestContext.CancellationToken).ConfigureAwait(false);

            string trustedPlatformAssemblies = AppContext.GetData(
                "TRUSTED_PLATFORM_ASSEMBLIES") as string
                ?? throw new InvalidOperationException(
                    "The test host did not provide trusted platform assemblies.");
            var loader = new SynchronizedWorkspaceLoader(trustedPlatformAssemblies.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries));
            snapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Microsoft.CodeAnalysis.Project[] projects =
            [
                .. snapshots
                    .SelectMany(static snapshot => snapshot.Solution.Projects)
                    .OrderBy(static project => project.Name, StringComparer.Ordinal)
            ];

            Assert.HasCount(3, projects);
            Microsoft.CodeAnalysis.Project fixture = projects.Single(static project =>
                project.Name == "Fixture");
            Assert.AreEqual(projectPath, fixture.FilePath);
            Assert.AreEqual(
                documentPath,
                Assert.ContainsSingle(fixture.Documents.Where(
                    static document => document.FilePath is not null)).FilePath);
            Microsoft.CodeAnalysis.Project library = projects.Single(static project =>
                project.Name == "Library");
            Assert.AreEqual(
                library.Id,
                Assert.ContainsSingle(fixture.ProjectReferences).ProjectId);
            Microsoft.CodeAnalysis.Project fileBasedApp = projects.Single(static project =>
                project.Name == "Tool.cs");
            Assert.AreEqual(fileBasedAppPath, fileBasedApp.FilePath);
            Assert.AreEqual(
                fileBasedAppPath,
                Assert.ContainsSingle(fileBasedApp.Documents.Where(
                    static document => document.FilePath is not null)).FilePath);
            foreach (Microsoft.CodeAnalysis.Project project in projects)
            {
                Microsoft.CodeAnalysis.Compilation compilation = await project
                    .GetCompilationAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Roslyn returned no compilation for {project.Name}.");
                Assert.IsEmpty(compilation.GetDiagnostics(TestContext.CancellationToken).Where(
                    static diagnostic =>
                        diagnostic.Severity >= Microsoft.CodeAnalysis.DiagnosticSeverity.Warning));
            }
        }
        finally
        {
            foreach (WorkspaceFolderSnapshot snapshot in snapshots)
            {
                snapshot.Workspace.Dispose();
            }

            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Retains multiple standalone projects in one real MSBuild workspace.
    /// </summary>
    [TestMethod]
    public async Task StandaloneProjectsShareOneRetainedMsBuildWorkspace()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-shared-msbuild-workspace-{Guid.NewGuid():N}");
        string alphaDirectory = Path.Join(workspacePath, "Alpha");
        string betaDirectory = Path.Join(workspacePath, "Beta");
        Directory.CreateDirectory(alphaDirectory);
        Directory.CreateDirectory(betaDirectory);
        IReadOnlyList<WorkspaceFolderSnapshot> snapshots = [];
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(alphaDirectory, "Alpha.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(alphaDirectory, "Program.cs"),
                DocumentText.Replace("Fixture", "Alpha", StringComparison.Ordinal),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(betaDirectory, "Beta.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(betaDirectory, "Program.cs"),
                DocumentText.Replace("Fixture", "Beta", StringComparison.Ordinal),
                TestContext.CancellationToken).ConfigureAwait(false);

            var loader = new MSBuildWorkspaceLoader(
                NullLogger<MSBuildWorkspaceLoader>.Instance);
            snapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceFolderSnapshot snapshot = Assert.ContainsSingle(
                snapshots,
                "One workspace root must not retain one MSBuild host per standalone project.");
            Assert.AreEqual(workspacePath, snapshot.RootPath);
            Assert.AreEqual(
                "Alpha,Beta",
                string.Join(
                    ',',
                    snapshot.Solution.Projects
                        .Select(static project => project.Name)
                        .Order(StringComparer.Ordinal)));
        }
        finally
        {
            foreach (WorkspaceFolderSnapshot snapshot in snapshots)
            {
                snapshot.Workspace.Dispose();
            }

            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Retains multiple file-based apps in one real MSBuild workspace.
    /// </summary>
    [TestMethod]
    public async Task FileBasedAppsShareOneRetainedMsBuildWorkspace()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-shared-file-app-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        IReadOnlyList<WorkspaceFolderSnapshot> snapshots = [];
        try
        {
            string alphaPath = Path.Join(workspacePath, "Alpha.cs");
            string betaPath = Path.Join(workspacePath, "Beta.cs");
            await File.WriteAllTextAsync(
                alphaPath,
                "#:property TargetFramework=net10.0\n\nConsole.WriteLine(\"alpha\");\n",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                betaPath,
                "#:property TargetFramework=net10.0\n\nConsole.WriteLine(\"beta\");\n",
                TestContext.CancellationToken).ConfigureAwait(false);

            var loader = new MSBuildWorkspaceLoader(
                NullLogger<MSBuildWorkspaceLoader>.Instance);
            snapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceFolderSnapshot snapshot = Assert.ContainsSingle(
                snapshots,
                "One workspace root must not retain one MSBuild host per file-based app.");
            Assert.AreEqual(
                "Alpha.cs,Beta.cs",
                string.Join(
                    ',',
                    snapshot.Solution.Projects
                        .Select(static project => project.Name)
                        .Order(StringComparer.Ordinal)));
            Assert.AreEqual(
                "Alpha.cs,Beta.cs",
                string.Join(
                    ',',
                    snapshot.Solution.Projects
                        .Select(static project => project.FilePath)
                        .OfType<string>()
                        .Select(Path.GetFileName)
                        .Order(StringComparer.Ordinal)));
        }
        finally
        {
            foreach (WorkspaceFolderSnapshot snapshot in snapshots)
            {
                snapshot.Workspace.Dispose();
            }

            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Loads solution projects and every discovered file-based app in one retained workspace.
    /// </summary>
    [TestMethod]
    public async Task SolutionLoadIncludesAllDiscoveredFileBasedApps()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-lazy-file-app-workspace-{Guid.NewGuid():N}");
        string projectDirectory = Path.Join(workspacePath, "src", "App");
        string scriptsDirectory = Path.Join(workspacePath, "scripts");
        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(scriptsDirectory);
        try
        {
            string projectPath = Path.Join(projectDirectory, "App.csproj");
            string openedScriptPath = Path.Join(scriptsDirectory, "Opened.cs");
            string unopenedScriptPath = Path.Join(scriptsDirectory, "Unopened.cs");
            const string scriptText =
                "#:property TargetFramework=net10.0\n\nConsole.WriteLine(\"script\");\n";
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(projectDirectory, "Program.cs"),
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                "<Solution><Project Path=\"src/App/App.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                openedScriptPath,
                scriptText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                unopenedScriptPath,
                scriptText,
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceInspectionSnapshot initial = await manager.InspectAsync(
                includeDiagnostics: false,
                diagnosticsProjectId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "App,Opened.cs,Unopened.cs",
                string.Join(
                    ',',
                    initial.Projects
                        .Select(static project => project.Name)
                        .Order(StringComparer.Ordinal)));
            WorkspaceBuildHostInspection buildHost = Assert.ContainsSingle(initial.BuildHosts);
            Assert.AreEqual(1, buildHost.WorkspaceCount);
            Assert.Contains(
                openedScriptPath,
                initial.Projects.Select(static project => project.FilePath));
            Assert.Contains(
                unopenedScriptPath,
                initial.Projects.Select(static project => project.FilePath));

            await manager.OpenDocumentAsync(
                new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(openedScriptPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = scriptText
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            await manager.ReloadAsync(TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceInspectionSnapshot reloaded = await manager.InspectAsync(
                includeDiagnostics: false,
                diagnosticsProjectId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "App,Opened.cs,Unopened.cs",
                string.Join(
                    ',',
                    reloaded.Projects
                        .Select(static project => project.Name)
                        .Order(StringComparer.Ordinal)));
            Assert.IsFalse(File.Exists(openedScriptPath + ".csproj"));
            Assert.IsFalse(File.Exists(unopenedScriptPath + ".csproj"));
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Registers an unchanged opened document without invalidating the semantic workspace.
    /// </summary>
    [TestMethod]
    public async Task OpeningUnchangedDocumentPreservesWorkspaceGeneration()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-unchanged-open-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string documentPath = await WriteSolutionAsync(
                workspacePath,
                "UnchangedOpen",
                TestContext.CancellationToken).ConfigureAwait(false);
            string documentText = await File.ReadAllTextAsync(
                documentPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);
            long loadedGeneration = manager.Generation;
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

            Assert.AreEqual(loadedGeneration, manager.Generation);
            await manager.ChangeDocumentAsync(
                new DidChangeTextDocumentParams
                {
                    TextDocument = new VersionedTextDocumentIdentifier
                    {
                        Uri = documentUri,
                        Version = 2
                    },
                    ContentChanges =
                    [
                        new TextDocumentContentChangeEvent
                        {
                            Text = documentText + Environment.NewLine
                        }
                    ]
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(loadedGeneration, manager.Generation);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Ignores delayed watcher events for the deleted SDK shadow project without hiding real projects.
    /// </summary>
    [TestMethod]
    public async Task FileBasedAppShadowProjectEventsDoNotReloadWorkspace()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-app-watcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string documentPath = Path.Join(workspacePath, "Program.cs");
            string shadowProjectPath = documentPath + ".csproj";
            const string fileBasedAppText = """
                #:property TargetFramework=net10.0
                Console.WriteLine("hello");
                """;
            await File.WriteAllTextAsync(
                documentPath,
                fileBasedAppText,
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);
            long loadedGeneration = manager.Generation;
            Assert.IsFalse(File.Exists(shadowProjectPath));

            foreach (FileChangeType changeType in new[]
            {
                FileChangeType.Created,
                FileChangeType.Deleted
            })
            {
                WorkspaceMaintenanceResult? maintenance = await manager.ApplyChangedFilesAsync(
                    new DidChangeWatchedFilesParams
                    {
                        Changes =
                        [
                            new FileEvent
                            {
                                Uri = DocumentUri.FromFileSystemPath(shadowProjectPath),
                                Type = changeType
                            }
                        ]
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(maintenance);
                Assert.AreEqual(loadedGeneration, manager.Generation);
            }

            await File.WriteAllTextAsync(
                shadowProjectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceMaintenanceResult? realProjectMaintenance =
                await manager.ApplyChangedFilesAsync(
                    new DidChangeWatchedFilesParams
                    {
                        Changes =
                        [
                            new FileEvent
                            {
                                Uri = DocumentUri.FromFileSystemPath(shadowProjectPath),
                                Type = FileChangeType.Created
                            }
                        ]
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(realProjectMaintenance);
            Assert.IsGreaterThan(loadedGeneration, manager.Generation);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Inspects nested solutions from one workspace while retaining stable diagnostic order.
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
            Assert.HasCount(
                1,
                snapshot.BuildHosts,
                "Two loaded workspaces in one process must not be reported as two build hosts.");
            WorkspaceBuildHostInspection buildHost = snapshot.BuildHosts[0];
            Assert.AreEqual(Environment.ProcessId, buildHost.ProcessId);
            Assert.AreEqual(
                1,
                buildHost.WorkspaceCount,
                "One workspace root must publish one retained Roslyn workspace.");
            Assert.AreEqual(2, buildHost.ProjectCount);
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
            await DirectoryReleaseWaiter.DeleteAsync(workspacePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
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

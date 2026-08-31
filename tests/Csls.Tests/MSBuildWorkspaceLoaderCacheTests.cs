using Csls.Workspaces;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;

namespace Csls.Tests;

/// <summary>
/// Verifies cached real MSBuild project states remain correct across workspace reloads.
/// </summary>
[TestClass]
public sealed class MSBuildWorkspaceLoaderCacheTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Keeps in-process MSBuild state from changing the language-server process directory.
    /// </summary>
    [TestMethod]
    public async Task ProjectLoadingPreservesProcessCurrentDirectory()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workspacePath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "test-fixtures",
            $"csls-msbuild-process-isolation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.csproj"),
                CreateProjectWithoutSymbolText(),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                "<Solution><Project Path=\"Fixture.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Program.cs"),
                "public sealed class Fixture;",
                TestContext.CancellationToken).ConfigureAwait(false);

            string expectedDirectory = Directory.GetCurrentDirectory();
            var observations = new ConcurrentQueue<string>();
            using var observationSource = new CancellationTokenSource();
            var observer = Task.Run(
                () => ObserveCurrentDirectory(
                    expectedDirectory,
                    observations,
                    observationSource.Token),
                CancellationToken.None);
            try
            {
                var loader = new MSBuildWorkspaceLoader(
                    NullLogger<MSBuildWorkspaceLoader>.Instance);
                IReadOnlyList<WorkspaceFolderSnapshot> snapshots = await loader.LoadAsync(
                    [workspacePath],
                    "Debug",
                    progress: null,
                    TestContext.CancellationToken).ConfigureAwait(false);
                using Workspace workspace = Assert.ContainsSingle(snapshots).Workspace;
                Assert.ContainsSingle(Assert.ContainsSingle(snapshots).Solution.Projects.Where(
                    project => string.Equals(
                        project.FilePath,
                        Path.Join(workspacePath, "Fixture.csproj"),
                        StringComparison.Ordinal)));
            }
            finally
            {
                await observationSource.CancelAsync().ConfigureAwait(false);
                await observer.ConfigureAwait(false);
            }

            Assert.IsEmpty(
                observations,
                $"MSBuild changed the process current directory:{Environment.NewLine}" +
                string.Join(Environment.NewLine, observations));
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Gives concurrent build hosts distinct temporary working directories and removes them.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentBuildHostsOwnTemporaryWorkingDirectories()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-msbuild-host-directories-{Guid.NewGuid():N}");
        string firstWorkspacePath = Path.Join(fixturePath, "First");
        string secondWorkspacePath = Path.Join(fixturePath, "Second");
        Directory.CreateDirectory(firstWorkspacePath);
        Directory.CreateDirectory(secondWorkspacePath);
        try
        {
            string firstObservationPath = Path.Join(firstWorkspacePath, "startup.txt");
            string secondObservationPath = Path.Join(secondWorkspacePath, "startup.txt");
            await File.WriteAllTextAsync(
                Path.Join(firstWorkspacePath, "First.csproj"),
                CreateStartupDirectoryProjectText(),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(secondWorkspacePath, "Second.csproj"),
                CreateStartupDirectoryProjectText(),
                TestContext.CancellationToken).ConfigureAwait(false);

            var firstLoader = new MSBuildWorkspaceLoader(
                NullLogger<MSBuildWorkspaceLoader>.Instance);
            var secondLoader = new MSBuildWorkspaceLoader(
                NullLogger<MSBuildWorkspaceLoader>.Instance);
            Task<IReadOnlyList<WorkspaceFolderSnapshot>> firstLoad = firstLoader.LoadAsync(
                [firstWorkspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken);
            Task<IReadOnlyList<WorkspaceFolderSnapshot>> secondLoad = secondLoader.LoadAsync(
                [secondWorkspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken);
            IReadOnlyList<WorkspaceFolderSnapshot>[] loads = await Task.WhenAll(
                firstLoad,
                secondLoad).ConfigureAwait(false);
            foreach (WorkspaceFolderSnapshot snapshot in loads.SelectMany(static load => load))
            {
                snapshot.Workspace.Dispose();
            }

            string firstWorkingDirectory = (await File.ReadAllTextAsync(
                firstObservationPath,
                TestContext.CancellationToken).ConfigureAwait(false)).Trim();
            string secondWorkingDirectory = (await File.ReadAllTextAsync(
                secondObservationPath,
                TestContext.CancellationToken).ConfigureAwait(false)).Trim();
            Assert.AreNotEqual(firstWorkingDirectory, secondWorkingDirectory);
            Assert.IsFalse(Directory.Exists(firstWorkingDirectory));
            Assert.IsFalse(Directory.Exists(secondWorkingDirectory));
            Assert.IsFalse(IsWithinDirectory(firstWorkingDirectory, fixturePath));
            Assert.IsFalse(IsWithinDirectory(secondWorkingDirectory, fixturePath));
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Reloads source text from disk and rebuilds project state after evaluated inputs change.
    /// </summary>
    [TestMethod]
    public async Task ReloadReflectsSourceAndProjectChanges()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-msbuild-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string projectPath = Path.Join(workspacePath, "Fixture.csproj");
            string solutionPath = Path.Join(workspacePath, "Fixture.slnx");
            string documentPath = Path.Join(workspacePath, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                CreateProjectText("FIRST"),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                solutionPath,
                "<Solution><Project Path=\"Fixture.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                "public sealed class Initial;",
                TestContext.CancellationToken).ConfigureAwait(false);

            var loader = new MSBuildWorkspaceLoader(
                NullLogger<MSBuildWorkspaceLoader>.Instance);
            IReadOnlyList<WorkspaceFolderSnapshot> initialSnapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceFolderSnapshot initialSnapshot = Assert.ContainsSingle(initialSnapshots);
            using (initialSnapshot.Workspace)
            {
                Project initialProject = Assert.ContainsSingle(
                    Assert.ContainsSingle(initialSnapshots).Solution.Projects);
                var parseOptions = (CSharpParseOptions)initialProject.ParseOptions!;
                Assert.Contains("FIRST", parseOptions.PreprocessorSymbolNames);
            }

            await File.WriteAllTextAsync(
                documentPath,
                "public sealed class Updated;",
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<WorkspaceFolderSnapshot> sourceSnapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceFolderSnapshot sourceSnapshot = Assert.ContainsSingle(sourceSnapshots);
            using (sourceSnapshot.Workspace)
            {
                Project sourceProject = Assert.ContainsSingle(
                    Assert.ContainsSingle(sourceSnapshots).Solution.Projects);
                Document sourceDocument = Assert.ContainsSingle(sourceProject.Documents.Where(
                    document => string.Equals(
                        document.FilePath,
                        documentPath,
                        StringComparison.Ordinal)));
                SourceText sourceText = await sourceDocument.GetTextAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("public sealed class Updated;", sourceText.ToString());
            }

            await File.WriteAllTextAsync(
                projectPath,
                CreateProjectText("SECOND"),
                TestContext.CancellationToken).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(
                projectPath,
                File.GetLastWriteTimeUtc(projectPath).AddSeconds(1));
            IReadOnlyList<WorkspaceFolderSnapshot> projectSnapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceFolderSnapshot projectSnapshot = Assert.ContainsSingle(projectSnapshots);
            using (projectSnapshot.Workspace)
            {
                Project reloadedProject = Assert.ContainsSingle(
                    Assert.ContainsSingle(projectSnapshots).Solution.Projects);
                var parseOptions = (CSharpParseOptions)reloadedProject.ParseOptions!;
                Assert.Contains("SECOND", parseOptions.PreprocessorSymbolNames);
                Assert.DoesNotContain("FIRST", parseOptions.PreprocessorSymbolNames);
            }

            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Added.cs"),
                "public sealed class Added;",
                TestContext.CancellationToken).ConfigureAwait(false);
            Directory.SetLastWriteTimeUtc(
                workspacePath,
                Directory.GetLastWriteTimeUtc(workspacePath).AddSeconds(1));
            IReadOnlyList<WorkspaceFolderSnapshot> addedSnapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceFolderSnapshot addedSnapshot = Assert.ContainsSingle(addedSnapshots);
            using (addedSnapshot.Workspace)
            {
                Project reloadedProject = Assert.ContainsSingle(
                    Assert.ContainsSingle(addedSnapshots).Solution.Projects);
                Assert.AreEqual(
                    "Added.cs,Program.cs",
                    string.Join(
                        ',',
                        reloadedProject.Documents
                            .Where(document => string.Equals(
                                Path.GetDirectoryName(document.FilePath),
                                workspacePath,
                                StringComparison.Ordinal))
                            .Select(static document => document.Name)
                            .Order(StringComparer.Ordinal)));
            }
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Invalidates project state when a previously absent ancestor build file is created.
    /// </summary>
    [TestMethod]
    public async Task ReloadReflectsNewAncestorBuildConfiguration()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-msbuild-ancestor-cache-{Guid.NewGuid():N}");
        string projectDirectory = Path.Join(workspacePath, "src");
        Directory.CreateDirectory(projectDirectory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(projectDirectory, "Fixture.csproj"),
                CreateProjectWithoutSymbolText(),
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                "<Solution><Project Path=\"src/Fixture.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(projectDirectory, "Program.cs"),
                "public sealed class Initial;",
                TestContext.CancellationToken).ConfigureAwait(false);

            var loader = new MSBuildWorkspaceLoader(
                NullLogger<MSBuildWorkspaceLoader>.Instance);
            IReadOnlyList<WorkspaceFolderSnapshot> initialSnapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceFolderSnapshot initialSnapshot = Assert.ContainsSingle(initialSnapshots);
            using (initialSnapshot.Workspace)
            {
                Project initialProject = Assert.ContainsSingle(
                    Assert.ContainsSingle(initialSnapshots).Solution.Projects);
                var parseOptions = (CSharpParseOptions)initialProject.ParseOptions!;
                Assert.DoesNotContain("ANCESTOR", parseOptions.PreprocessorSymbolNames);
            }

            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Directory.Build.props"),
                """
                <Project>
                  <PropertyGroup>
                    <DefineConstants>$(DefineConstants);ANCESTOR</DefineConstants>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<WorkspaceFolderSnapshot> updatedSnapshots = await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceFolderSnapshot updatedSnapshot = Assert.ContainsSingle(updatedSnapshots);
            using (updatedSnapshot.Workspace)
            {
                Project updatedProject = Assert.ContainsSingle(
                    Assert.ContainsSingle(updatedSnapshots).Solution.Projects);
                var parseOptions = (CSharpParseOptions)updatedProject.ParseOptions!;
                Assert.Contains("ANCESTOR", parseOptions.PreprocessorSymbolNames);
            }
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static string CreateProjectText(string symbol) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <DefineConstants>{{symbol}}</DefineConstants>
          </PropertyGroup>
        </Project>
        """;

    private static void ObserveCurrentDirectory(
        string expectedDirectory,
        ConcurrentQueue<string> observations,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string currentDirectory = Directory.GetCurrentDirectory();
                if (!string.Equals(
                    currentDirectory,
                    expectedDirectory,
                    StringComparison.Ordinal))
                {
                    observations.Enqueue(currentDirectory);
                    return;
                }
            }
            catch (IOException exception)
            {
                observations.Enqueue(exception.Message);
                return;
            }

            _ = Thread.Yield();
        }
    }

    private static string CreateProjectWithoutSymbolText() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private static string CreateStartupDirectoryProjectText() => """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <Target Name="CaptureStartupDirectory" BeforeTargets="Compile">
            <WriteLinesToFile
                File="$(MSBuildProjectDirectory)/startup.txt"
                Lines="$(MSBuildStartupDirectory)"
                Overwrite="true" />
          </Target>
        </Project>
        """;

    private static bool IsWithinDirectory(string path, string directory)
    {
        string relativePath = Path.GetRelativePath(directory, path);
        return !Path.IsPathFullyQualified(relativePath) &&
            relativePath is not ".." &&
            !relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal);
    }
}

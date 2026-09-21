using Csls.Workspaces;
using Microsoft.CodeAnalysis;

namespace Csls.Tests;

/// <summary>
/// Verifies workspace loaders share metadata images across projects and reloads.
/// </summary>
[TestClass]
public sealed class WorkspaceMetadataReferenceTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Shares synchronized project references within and across real workspace loads.
    /// </summary>
    [TestMethod]
    public async Task SynchronizedProjectsShareMetadataReferencesAcrossLoads()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-synchronized-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            const string projectText = """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "First.csproj"),
                projectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Second.csproj"),
                projectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Fixture.slnx"),
                "<Solution><Project Path=\"First.csproj\" /><Project Path=\"Second.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(workspacePath, "Program.cs"),
                "public sealed class Fixture;",
                TestContext.CancellationToken).ConfigureAwait(false);

            string referencePath = typeof(object).Assembly.Location;
            var loader = new SynchronizedWorkspaceLoader([referencePath]);
            WorkspaceFolderSnapshot initial = Assert.ContainsSingle(await loader.LoadAsync(
                [workspacePath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false));
            using (initial.Workspace)
            {
                Project[] projects = [.. initial.Solution.Projects];
                Assert.HasCount(2, projects);
                PortableExecutableReference firstReference = GetReference(projects[0]);
                Assert.AreSame(firstReference, GetReference(projects[1]));

                WorkspaceFolderSnapshot reloaded = Assert.ContainsSingle(await loader.LoadAsync(
                    [workspacePath],
                    "Debug",
                    progress: null,
                    TestContext.CancellationToken).ConfigureAwait(false));
                using (reloaded.Workspace)
                {
                    Assert.AreSame(
                        firstReference,
                        GetReference(Assert.ContainsSingle(
                            reloaded.Solution.Projects.Where(project =>
                                project.Name == "First"))));
                }
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                workspacePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Shares loose-file references between folders and successive loads.
    /// </summary>
    [TestMethod]
    public async Task LooseFilesShareMetadataReferencesAcrossLoads()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-loose-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string firstPath = Path.Join(workspacePath, "First.cs");
            string secondPath = Path.Join(workspacePath, "Second.cs");
            await File.WriteAllTextAsync(
                firstPath,
                "public sealed class First;",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                secondPath,
                "public sealed class Second;",
                TestContext.CancellationToken).ConfigureAwait(false);

            var loader = new LooseFileWorkspaceLoader([typeof(object).Assembly.Location]);
            IReadOnlyList<WorkspaceFolderSnapshot> initial = await loader.LoadAsync(
                [firstPath, secondPath],
                "Debug",
                progress: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            try
            {
                Assert.HasCount(2, initial);
                PortableExecutableReference firstReference = GetReference(
                    Assert.ContainsSingle(initial[0].Solution.Projects));
                Assert.AreSame(
                    firstReference,
                    GetReference(Assert.ContainsSingle(initial[1].Solution.Projects)));

                IReadOnlyList<WorkspaceFolderSnapshot> reloaded = await loader.LoadAsync(
                    [firstPath],
                    "Debug",
                    progress: null,
                    TestContext.CancellationToken).ConfigureAwait(false);
                try
                {
                    Assert.AreSame(
                        firstReference,
                        GetReference(Assert.ContainsSingle(
                            Assert.ContainsSingle(reloaded).Solution.Projects)));
                }
                finally
                {
                    foreach (WorkspaceFolderSnapshot snapshot in reloaded)
                    {
                        snapshot.Workspace.Dispose();
                    }
                }
            }
            finally
            {
                foreach (WorkspaceFolderSnapshot snapshot in initial)
                {
                    snapshot.Workspace.Dispose();
                }
            }
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                workspacePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static PortableExecutableReference GetReference(Project project) =>
        Assert.ContainsSingle(project.MetadataReferences.OfType<PortableExecutableReference>());
}

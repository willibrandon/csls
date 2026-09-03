using Csls.Protocol;
using Csls.Workspaces;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies project ownership when documents open before workspace watcher notifications arrive.
/// </summary>
[TestClass]
public sealed class WorkspaceDocumentOpeningTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Keeps a newly created project source in its owning project when didOpen wins the watcher race.
    /// </summary>
    [TestMethod]
    public async Task ProjectSourceOpenedBeforeWatcherRemainsInOwningProject()
    {
        string workspacePath = Path.Join(
            Path.GetTempPath(),
            $"csls-open-before-watcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        try
        {
            string projectPath = Path.Join(workspacePath, "Fixture.csproj");
            string solutionPath = Path.Join(workspacePath, "Fixture.slnx");
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                solutionPath,
                "<Solution><Project Path=\"Fixture.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [workspacePath],
                TestContext.CancellationToken).ConfigureAwait(false);

            string documentPath = Path.Join(workspacePath, "NewService.cs");
            const string documentText =
                "namespace Fixture; internal sealed class NewService;";
            await File.WriteAllTextAsync(
                documentPath,
                documentText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await manager.OpenDocumentAsync(
                new TextDocumentItem
                {
                    Uri = DocumentUri.FromFileSystemPath(documentPath),
                    LanguageId = "csharp",
                    Version = 1,
                    Text = documentText
                },
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceInspectionSnapshot snapshot = await manager.InspectAsync(
                includeDiagnostics: false,
                diagnosticsProjectId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceProjectInspection project = Assert.ContainsSingle(snapshot.Projects);
            Assert.AreEqual("Fixture", project.Name);
            Assert.AreEqual(projectPath, project.FilePath);
            Assert.AreEqual(1, snapshot.BuildHosts[0].ProjectCount);
            Assert.IsFalse(File.Exists(documentPath + ".csproj"));
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                workspacePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}

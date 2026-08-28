using Csls.Protocol;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies editor workspace inspection and maintenance through a real language-server process.
/// </summary>
[TestClass]
public sealed class WorkspaceInfoLanguageServerTests
{
    private static readonly TimeSpan s_workspaceTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Reports real Roslyn projects and reloads them after a real restore.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceInfoAndRestoreUseLiveRoslynState()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = EditorToolResolver.ResolveServerWorker(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-info-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string projectPath = Path.Join(fixturePath, "WorkspaceInfoFixture.csproj");
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-workspace-info-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);

            CSharpWorkspaceInfo initial = await WaitForWorkspaceAsync(
                lsp,
                TestContext.CancellationToken).ConfigureAwait(false);
            CSharpWorkspaceFolderInfo folder = Assert.ContainsSingle(initial.Workspaces);
            Assert.AreEqual(Path.GetFullPath(fixturePath), folder.RootPath);
            Assert.AreEqual(1, folder.ProjectCount);
            CSharpWorkspaceProjectInfo project = Assert.ContainsSingle(initial.Projects);
            Assert.AreEqual("WorkspaceInfoFixture", project.Name);
            Assert.AreEqual(Path.GetFullPath(projectPath), project.FilePath);
            CSharpWorkspaceDocumentInfo document = Assert.ContainsSingle(
                initial.Documents.Where(static document => document.Name == "Program.cs"));
            Assert.AreEqual(project.Id, document.ProjectId);

            CSharpWorkspaceOperationInfo operation = await lsp.RestoreWorkspaceForClientAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("restore", operation.Operation);
            Assert.AreEqual(initial.Generation, operation.PreviousGeneration);
            Assert.IsGreaterThan(operation.PreviousGeneration, operation.CurrentGeneration);
            Assert.AreEqual(1, operation.AffectedWorkspaceCount);
            Assert.AreEqual(1, operation.RestoredEntryPointCount);

            CSharpWorkspaceInfo restored = await lsp.RequestWorkspaceInfoAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(operation.CurrentGeneration, restored.Generation);
            Assert.AreEqual("WorkspaceInfoFixture", Assert.ContainsSingle(restored.Projects).Name);

            await lsp.RequestShutdownAsync(TestContext.CancellationToken).ConfigureAwait(false);
            string diagnostics = await lsp.ExitAsync(
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
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string DocumentText = """
        Console.WriteLine("workspace info");
        """;

    private static async Task<CSharpWorkspaceInfo> WaitForWorkspaceAsync(
        LspProcessSession lsp,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(s_workspaceTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        while (true)
        {
            CSharpWorkspaceInfo info = await lsp.RequestWorkspaceInfoAsync(
                linked.Token).ConfigureAwait(false);
            if (info.Projects.Count > 0)
            {
                return info;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), linked.Token).ConfigureAwait(false);
        }
    }
}

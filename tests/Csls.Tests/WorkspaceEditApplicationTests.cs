using Csls.Protocol;
using Csls.Workspaces;
using System.Runtime.CompilerServices;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies transactional workspace-edit application against real SDK projects and files.
/// </summary>
[TestClass]
public sealed class WorkspaceEditApplicationTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Restores every committed file when the post-create project reload fails.
    /// </summary>
    [TestMethod]
    public async Task ResourceEditRollsBackWhenProjectReloadFails()
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-resource-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string projectPath = Path.Join(fixturePath, "Fixture.csproj");
            string solutionPath = Path.Join(fixturePath, "Fixture.slnx");
            string sourcePath = Path.Join(fixturePath, "Program.cs");
            string targetPath = Path.Join(fixturePath, "Helper.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                sourcePath,
                SourceText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                solutionPath,
                SolutionText,
                TestContext.CancellationToken).ConfigureAwait(false);

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using ConfiguredAsyncDisposable managerDisposal =
                manager.ConfigureAwait(false);
            await manager.LoadAsync(
                [fixturePath],
                TestContext.CancellationToken).ConfigureAwait(false);
            IReadOnlyList<CodeAction> actions = await manager.GetCodeActionsAsync(
                new CodeActionParams
                {
                    TextDocument = new TextDocumentIdentifier
                    {
                        Uri = DocumentUri.FromFileSystemPath(sourcePath)
                    },
                    Range = new LspRange(
                        new Position(7, 22),
                        new Position(7, 28)),
                    Context = new CodeActionContext
                    {
                        Diagnostics = [],
                        Only = ["refactor"]
                    }
                },
                supportsCreateFile: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceEdit edit = Assert.ContainsSingle(actions).Edit
                ?? throw new InvalidDataException("The move action had no workspace edit.");
            WorkspaceEditSnapshot snapshot = await manager.CreateEditSnapshotAsync(
                edit,
                TestContext.CancellationToken).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                solutionPath,
                "<Solution",
                TestContext.CancellationToken).ConfigureAwait(false);
            Exception exception = await Assert.ThrowsAsync<Exception>(() =>
                manager.ApplyWorkspaceEditAsync(
                    snapshot,
                    TestContext.CancellationToken)).ConfigureAwait(false);
            Assert.Contains("project", exception.ToString(), StringComparison.OrdinalIgnoreCase);

            Assert.IsFalse(File.Exists(targetPath));
            Assert.AreEqual(
                SourceText,
                await File.ReadAllTextAsync(
                    sourcePath,
                    TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(Directory.EnumerateFiles(fixturePath, "*.csls-*"));
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
          </PropertyGroup>
        </Project>
        """;

    private const string SolutionText = """
        <Solution>
          <Project Path="Fixture.csproj" />
        </Solution>
        """;

    private const string SourceText = """
        namespace Fixture;

        public static class Program
        {
            public static int Read() => Helper.Value;
        }

        internal static class Helper
        {
            public static int Value => 42;
        }
        """;
}

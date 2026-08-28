using Csls.Protocol;
using Csls.Workspaces;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies Unity project discovery through a real out-of-process language server.
/// </summary>
[TestClass]
public sealed class UnityWorkspaceLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Loads a nested Unity project without traversing its generated workspace trees.
    /// </summary>
    [TestMethod]
    public async Task GeneratedUnityDirectoriesAreExcluded()
    {
        string workspacePath = CreateFixturePath("generated-directories");
        string unityPath = Path.Join(workspacePath, "Game");
        string assetsPath = Path.Join(unityPath, "Assets", "Scripts");
        string projectSettingsPath = Path.Join(unityPath, "ProjectSettings");
        Directory.CreateDirectory(assetsPath);
        Directory.CreateDirectory(projectSettingsPath);
        try
        {
            string controllerPath = Path.Join(assetsPath, "PlayerController.cs");
            await File.WriteAllTextAsync(
                Path.Join(projectSettingsPath, "ProjectVersion.txt"),
                "m_EditorVersion: 6000.0.0f1\n",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(unityPath, "Assembly-CSharp.csproj"),
                UnityProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(unityPath, "Game.slnx"),
                "<Solution><Project Path=\"Assembly-CSharp.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(assetsPath, "MonoBehaviour.cs"),
                MonoBehaviourText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                controllerPath,
                PlayerControllerText,
                TestContext.CancellationToken).ConfigureAwait(false);

            string generatedSolutionPath = Path.Join(
                unityPath,
                "Library",
                "com.vendor.hotreload",
                "Solution");
            Directory.CreateDirectory(generatedSolutionPath);
            string generatedWorkspacePath = Path.Join(
                generatedSolutionPath,
                "Generated.slnx");
            await File.WriteAllTextAsync(
                generatedWorkspacePath,
                "<invalid>",
                TestContext.CancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(Path.Join(unityPath, "Temp", "Build"));
            Directory.CreateDirectory(Path.Join(unityPath, "Logs", "Packages"));
            Directory.CreateDirectory(Path.Join(unityPath, "UserSettings", "Layouts"));

            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using (manager.ConfigureAwait(false))
            {
                await manager.LoadAsync(
                    [workspacePath],
                    TestContext.CancellationToken).ConfigureAwait(false);
                long generation = manager.Generation;
                WorkspaceMaintenanceResult? result = await manager.ApplyCreatedFilesAsync(
                    new CreateFilesParams
                    {
                        Files =
                        [
                            new FileCreate
                            {
                                Uri = DocumentUri.FromFileSystemPath(generatedWorkspacePath)
                            }
                        ]
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(result);
                Assert.AreEqual(generation, manager.Generation);
            }

            LspProcessSession lsp = StartWorker(workspacePath);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                workspacePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(controllerPath, PlayerControllerText)
                .ConfigureAwait(false);

            JsonElement hoverElement = await lsp.RequestHoverAsync(
                controllerPath,
                new Position(4, 40),
                TestContext.CancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The Unity source returned no hover.");
            Hover hover = hoverElement.Deserialize(LspJsonSerializerContext.Default.Hover)
                ?? throw new InvalidDataException("The Unity source returned invalid hover.");
            Assert.Contains("UnityEngine.MonoBehaviour", hover.Contents.Value);
            WorkspaceSymbol controller = Assert.ContainsSingle(
                await lsp.RequestWorkspaceSymbolsAsync(
                    "PlayerController",
                    TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(controllerPath),
                controller.Location.Uri);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    /// <summary>
    /// Preserves ordinary .NET workspaces stored in a directory named Library.
    /// </summary>
    [TestMethod]
    public async Task NonUnityLibraryDirectoriesRemainDiscoverable()
    {
        string workspacePath = CreateFixturePath("library-project");
        string projectPath = Path.Join(workspacePath, "Library");
        Directory.CreateDirectory(projectPath);
        try
        {
            string documentPath = Path.Join(projectPath, "LibraryType.cs");
            await File.WriteAllTextAsync(
                Path.Join(projectPath, "Library.csproj"),
                LibraryProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(projectPath, "Library.slnx"),
                "<Solution><Project Path=\"Library.csproj\" /></Solution>",
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                LibraryDocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            string createdPath = Path.Join(projectPath, "CreatedLibraryType.cs");
            WorkspaceManager manager = WorkspaceManagerTestFactory.Create();
            await using (manager.ConfigureAwait(false))
            {
                await manager.LoadAsync(
                    [workspacePath],
                    TestContext.CancellationToken).ConfigureAwait(false);
                long generation = manager.Generation;
                await File.WriteAllTextAsync(
                    createdPath,
                    CreatedLibraryDocumentText,
                    TestContext.CancellationToken).ConfigureAwait(false);
                WorkspaceMaintenanceResult? result = await manager.ApplyCreatedFilesAsync(
                    new CreateFilesParams
                    {
                        Files =
                        [
                            new FileCreate
                            {
                                Uri = DocumentUri.FromFileSystemPath(createdPath)
                            }
                        ]
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNotNull(result);
                Assert.IsGreaterThan(generation, manager.Generation);
                WorkspaceSymbol createdSymbol = Assert.ContainsSingle(
                    await manager.GetWorkspaceSymbolsAsync(
                        new WorkspaceSymbolParams { Query = "CreatedLibraryType" },
                        TestContext.CancellationToken).ConfigureAwait(false));
                Assert.AreEqual(
                    DocumentUri.FromFileSystemPath(createdPath),
                    createdSymbol.Location.Uri);
            }

            LspProcessSession lsp = StartWorker(workspacePath);
            await using ConfiguredAsyncDisposable lspDisposal = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                workspacePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            IReadOnlyList<WorkspaceSymbol> symbols = await lsp.RequestWorkspaceSymbolsAsync(
                "LibraryType",
                TestContext.CancellationToken).ConfigureAwait(false);
            WorkspaceSymbol symbol = Assert.ContainsSingle(
                symbols.Where(static candidate => candidate.Name == "LibraryType"));
            Assert.AreEqual(
                DocumentUri.FromFileSystemPath(documentPath),
                symbol.Location.Uri);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    private static LspProcessSession StartWorker(string workingDirectory)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        return LspProcessSession.Start(
            "csls-unity-workspace-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            workingDirectory);
    }

    private static string CreateFixturePath(string name) => Path.Join(
        Path.GetTempPath(),
        $"csls-unity-{name}-{Guid.NewGuid():N}");

    private const string UnityProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
          </PropertyGroup>
          <ItemGroup>
            <Compile Include="Assets/**/*.cs" />
          </ItemGroup>
        </Project>
        """;

    private const string MonoBehaviourText = """
        namespace UnityEngine;

        public abstract class MonoBehaviour;
        """;

    private const string PlayerControllerText = """
        using UnityEngine;

        namespace Game;

        public sealed class PlayerController : MonoBehaviour
        {
            public int Speed => Math.Abs(-5);
        }
        """;

    private const string LibraryProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string LibraryDocumentText = """
        namespace Fixture;

        public sealed class LibraryType
        {
            public int Value => Math.Abs(-1);
        }
        """;

    private const string CreatedLibraryDocumentText = """
        namespace Fixture;

        public sealed class CreatedLibraryType;
        """;
}

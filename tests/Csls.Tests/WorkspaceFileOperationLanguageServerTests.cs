using Csls.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies workspace file-operation synchronization through a real language-server worker.
/// </summary>
[TestClass]
public sealed class WorkspaceFileOperationLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Tracks created, renamed, moved, and deleted sources without retaining stale documents.
    /// </summary>
    [TestMethod]
    public async Task FileOperationsReloadProjectsAndRemapOpenOverlays()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-operations-{Guid.NewGuid():N}");
        string modelsPath = Path.Join(fixturePath, "Models");
        Directory.CreateDirectory(modelsPath);
        try
        {
            string projectPath = Path.Join(fixturePath, "Fixture.csproj");
            string consumerPath = Path.Join(fixturePath, "Consumer.cs");
            string existingPath = Path.Join(modelsPath, "Existing.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                consumerPath,
                ConsumerText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                existingPath,
                ExistingText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-file-operation-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var clientCapabilities = JsonDocument.Parse(
                """
                {
                  "workspace": {
                    "fileOperations": {
                      "didCreate": true,
                      "didRename": true,
                      "didDelete": true
                    }
                  },
                  "textDocument": {
                    "diagnostic": {}
                  }
                }
                """);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                clientCapabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            AssertFileOperationCapabilities(initialization);

            await lsp.OpenDocumentAsync(existingPath, ExistingText).ConfigureAwait(false);
            await lsp.ChangeDocumentAsync(
                existingPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = OverlayText }])
                .ConfigureAwait(false);

            string createdPath = Path.Join(modelsPath, "Created.cs");
            await File.WriteAllTextAsync(
                createdPath,
                CreatedText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CreateFilesAsync([createdPath]).ConfigureAwait(false);
            AssertSymbolPath(
                await lsp.RequestWorkspaceSymbolsAsync(
                    "CreatedType",
                    TestContext.CancellationToken).ConfigureAwait(false),
                createdPath);
            AssertSymbolPath(
                await lsp.RequestWorkspaceSymbolsAsync(
                    "OverlayValue",
                    TestContext.CancellationToken).ConfigureAwait(false),
                existingPath);

            string movedModelsPath = Path.Join(fixturePath, "MovedModels");
            Directory.Move(modelsPath, movedModelsPath);
            await lsp.RenameFilesAsync([(modelsPath, movedModelsPath)]).ConfigureAwait(false);
            string movedExistingPath = Path.Join(movedModelsPath, "Existing.cs");
            string movedCreatedPath = Path.Join(movedModelsPath, "Created.cs");
            AssertSymbolPath(
                await lsp.RequestWorkspaceSymbolsAsync(
                    "ExistingType",
                    TestContext.CancellationToken).ConfigureAwait(false),
                movedExistingPath);
            AssertSymbolPath(
                await lsp.RequestWorkspaceSymbolsAsync(
                    "OverlayValue",
                    TestContext.CancellationToken).ConfigureAwait(false),
                movedExistingPath);
            await lsp.ChangeDocumentAsync(
                movedExistingPath,
                version: 3,
                [new TextDocumentContentChangeEvent { Text = UpdatedOverlayText }])
                .ConfigureAwait(false);

            string returnedExistingPath = Path.Join(fixturePath, "Existing.cs");
            File.Move(movedExistingPath, returnedExistingPath);
            await lsp.RenameFilesAsync([(movedExistingPath, returnedExistingPath)])
                .ConfigureAwait(false);
            await lsp.ChangeDocumentAsync(
                returnedExistingPath,
                version: 4,
                [new TextDocumentContentChangeEvent { Text = FinalOverlayText }])
                .ConfigureAwait(false);
            AssertSymbolPath(
                await lsp.RequestWorkspaceSymbolsAsync(
                    "OverlayValue",
                    TestContext.CancellationToken).ConfigureAwait(false),
                returnedExistingPath);

            string caseRenamedExistingPath = Path.Join(fixturePath, "existing.cs");
            File.Move(returnedExistingPath, caseRenamedExistingPath);
            await lsp.RenameFilesAsync([(returnedExistingPath, caseRenamedExistingPath)])
                .ConfigureAwait(false);
            await lsp.ChangeDocumentAsync(
                caseRenamedExistingPath,
                version: 5,
                [new TextDocumentContentChangeEvent { Text = FinalOverlayText }])
                .ConfigureAwait(false);
            AssertSymbolPath(
                await lsp.RequestWorkspaceSymbolsAsync(
                    "OverlayValue",
                    TestContext.CancellationToken).ConfigureAwait(false),
                caseRenamedExistingPath);

            File.Delete(movedCreatedPath);
            await lsp.DeleteFilesAsync([movedCreatedPath]).ConfigureAwait(false);
            Assert.IsEmpty(await lsp.RequestWorkspaceSymbolsAsync(
                "CreatedType",
                TestContext.CancellationToken).ConfigureAwait(false));
            Directory.Delete(movedModelsPath);
            await lsp.DeleteFilesAsync([movedModelsPath]).ConfigureAwait(false);

            IReadOnlyList<WorkspaceSymbol> existingSymbols =
                await lsp.RequestWorkspaceSymbolsAsync(
                    "ExistingType",
                    TestContext.CancellationToken).ConfigureAwait(false);
            AssertSymbolPath(existingSymbols, caseRenamedExistingPath);
            DocumentDiagnosticReport diagnostics = await lsp.RequestDiagnosticsAsync(
                consumerPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "CS0101",
                diagnostics.Items?.Select(static diagnostic => diagnostic.Code) ?? []);
            Assert.DoesNotContain(
                "CS0117",
                diagnostics.Items?.Select(static diagnostic => diagnostic.Code) ?? []);

            string serverDiagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                serverDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    /// <summary>
    /// Republishes diagnostics for open documents affected by file topology changes.
    /// </summary>
    [TestMethod]
    public async Task FileOperationsRefreshPushDiagnostics()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = ResolveWorkerPath(repositoryRoot);
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-file-operation-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string consumerPath = Path.Join(fixturePath, "Consumer.cs");
            string createdPath = Path.Join(fixturePath, "Created.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                consumerPath,
                PushConsumerText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var client = new LspTestClient(
                legacyConfiguration: null,
                preferredConfiguration: null);
            var lsp = LspProcessSession.Start(
                "csls-file-operation-push-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var clientCapabilities = JsonDocument.Parse(
                """
                {
                  "workspace": {
                    "fileOperations": {
                      "didCreate": true,
                      "didDelete": true
                    }
                  }
                }
                """);
            await lsp.InitializeAsync(
                fixturePath,
                clientCapabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(consumerPath, PushConsumerText).ConfigureAwait(false);
            PublishDiagnosticsParams missing = await client.ReadPublishedDiagnosticsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "CS0103",
                missing.Diagnostics.Select(static diagnostic => diagnostic.Code));

            await File.WriteAllTextAsync(
                createdPath,
                CreatedTextWithValue,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CreateFilesAsync([createdPath]).ConfigureAwait(false);
            PublishDiagnosticsParams created = await client.ReadPublishedDiagnosticsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(consumerPath), created.Uri);
            Assert.AreEqual(1, created.Version);
            Assert.DoesNotContain(
                "CS0103",
                created.Diagnostics.Select(static diagnostic => diagnostic.Code));

            File.Delete(createdPath);
            await lsp.DeleteFilesAsync([createdPath]).ConfigureAwait(false);
            PublishDiagnosticsParams deleted = await client.ReadPublishedDiagnosticsAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DocumentUri.FromFileSystemPath(consumerPath), deleted.Uri);
            Assert.AreEqual(1, deleted.Version);
            Assert.Contains(
                "CS0103",
                deleted.Diagnostics.Select(static diagnostic => diagnostic.Code));

            string serverDiagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                serverDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private static void AssertFileOperationCapabilities(JsonElement initialization)
    {
        JsonElement fileOperations = initialization
            .GetProperty("capabilities")
            .GetProperty("workspace")
            .GetProperty("fileOperations");
        Assert.IsTrue(fileOperations.TryGetProperty("didCreate", out _));
        Assert.IsTrue(fileOperations.TryGetProperty("didRename", out _));
        Assert.IsTrue(fileOperations.TryGetProperty("didDelete", out _));
        Assert.IsFalse(fileOperations.TryGetProperty("willCreate", out _));
        Assert.IsFalse(fileOperations.TryGetProperty("willRename", out _));
        Assert.IsFalse(fileOperations.TryGetProperty("willDelete", out _));

        JsonElement[] filters =
        [
            .. fileOperations
                .GetProperty("didCreate")
                .GetProperty("filters")
                .EnumerateArray()
        ];
        Assert.HasCount(3, filters);
        Assert.AreEqual("file", filters[0].GetProperty("scheme").GetString());
        Assert.AreEqual(
            "**/*.{cs,csx,cshtml,razor,csproj,sln,slnx,props,targets,ruleset,globalconfig}",
            filters[0].GetProperty("pattern").GetProperty("glob").GetString());
        Assert.AreEqual(
            FileOperationPatternKind.File,
            filters[0].GetProperty("pattern").GetProperty("matches").GetString());
        Assert.AreEqual(
            OperatingSystem.IsWindows(),
            filters[0]
                .GetProperty("pattern")
                .GetProperty("options")
                .GetProperty("ignoreCase")
                .GetBoolean());
        Assert.AreEqual(
            FileOperationPatternKind.Folder,
            filters[2].GetProperty("pattern").GetProperty("matches").GetString());
    }

    private static void AssertSymbolPath(
        IReadOnlyList<WorkspaceSymbol> symbols,
        string expectedPath)
    {
        WorkspaceSymbol symbol = Assert.ContainsSingle(symbols);
        Assert.AreEqual(
            DocumentUri.FromFileSystemPath(expectedPath),
            symbol.Location.Uri);
    }

    private static string ResolveWorkerPath(string repositoryRoot)
    {
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        return workerPath;
    }

    private const string ProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string ExistingText = """
        namespace Fixture;

        public sealed class ExistingType
        {
            public static int PersistedValue => 1;
        }
        """;

    private const string OverlayText = """
        namespace Fixture;

        public sealed class ExistingType
        {
            public static int PersistedValue => 1;

            public static int OverlayValue => 2;
        }
        """;

    private const string UpdatedOverlayText = """
        namespace Fixture;

        public sealed class ExistingType
        {
            public static int PersistedValue => 1;

            public static int OverlayValue => 3;
        }
        """;

    private const string FinalOverlayText = """
        namespace Fixture;

        public sealed class ExistingType
        {
            public static int PersistedValue => 1;

            public static int OverlayValue => 4;
        }
        """;

    private const string CreatedText = """
        namespace Fixture;

        public sealed class CreatedType
        {
        }
        """;

    private const string CreatedTextWithValue = """
        namespace Fixture;

        public static class CreatedType
        {
            public static int Value => 42;
        }
        """;

    private const string ConsumerText = """
        namespace Fixture;

        public static class Consumer
        {
            public static int Read() => ExistingType.OverlayValue;
        }
        """;

    private const string PushConsumerText = """
        namespace Fixture;

        public static class Consumer
        {
            public static int Read() => CreatedType.Value;
        }
        """;
}

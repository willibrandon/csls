using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using StreamJsonRpc;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LspRange = Csls.Protocol.Range;

namespace Csls.Tests;

/// <summary>
/// Verifies workspace pull diagnostics through a real multi-project language-server process.
/// </summary>
[TestClass]
public sealed class WorkspaceDiagnosticLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Completes startup requests promptly and refreshes diagnostics after loading.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceRequestsCompleteDuringInitializationAndRefreshWhenReady()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-diagnostic-loading-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string documentPath = Path.Join(fixturePath, "Broken.cs");
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                CoreProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                InvalidCoreText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var client = new LspTestClient(
                legacyConfiguration: null,
                preferredConfiguration: null);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-workspace-diagnostic-loading-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "workspace": {
                    "diagnostics": {
                      "refreshSupport": true
                    }
                  },
                  "textDocument": {
                    "diagnostic": {}
                  }
                }
                """);
            await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            string socketPath = ControlEndpoint.GetSocketPath(lsp.ProcessId);
            await WaitForFileAsync(
                socketPath,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken).ConfigureAwait(false);
            var control = new ControlRpcClient(socketPath);
            await using ConfiguredAsyncDisposable controlCleanup =
                control.ConfigureAwait(false);

            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await WaitForWorkspacePhaseAsync(
                control,
                "Loading",
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken).ConfigureAwait(false);
            var loadingPosition = new Position(0, 0);
            var loadingRange = new LspRange(
                loadingPosition,
                loadingPosition);
            Task<LinkedEditingRanges?> loadingLinkedEditing =
                lsp.RequestLinkedEditingRangesAsync(
                    documentPath,
                    loadingPosition,
                    TestContext.CancellationToken);
            Task<IReadOnlyList<DocumentHighlight>> loadingHighlights =
                lsp.RequestDocumentHighlightsAsync(
                    documentPath,
                    loadingPosition,
                    TestContext.CancellationToken);
            Task<IReadOnlyList<DocumentLink>> loadingLinks =
                lsp.RequestDocumentLinksAsync(
                    documentPath,
                    TestContext.CancellationToken);
            Task<IReadOnlyList<CodeAction>> loadingActions =
                lsp.RequestCodeActionsAsync(
                    documentPath,
                    loadingRange,
                    only: null,
                    TestContext.CancellationToken);
            await Task.WhenAll(
                    loadingLinkedEditing,
                    loadingHighlights,
                    loadingLinks,
                    loadingActions)
                .WaitAsync(TimeSpan.FromSeconds(1), TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsNull(await loadingLinkedEditing.ConfigureAwait(false));
            Assert.IsEmpty(await loadingHighlights.ConfigureAwait(false));
            Assert.IsEmpty(await loadingLinks.ConfigureAwait(false));
            Assert.IsEmpty(await loadingActions.ConfigureAwait(false));
            DocumentDiagnosticReport loadingDocument = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("full", loadingDocument.Kind);
            Assert.IsEmpty(
                loadingDocument.Items ?? [],
                "The initial document pull waited for project loading and returned stale startup results.");
            WorkspaceDiagnosticReport loading = await lsp.RequestWorkspaceDiagnosticsAsync(
                [],
                partialResultToken: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(
                loading.Items,
                "The initial workspace pull waited for project loading and returned stale startup results.");

            await client.WaitForDiagnosticRefreshAsync(TestContext.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.CancellationToken)
                .ConfigureAwait(false);
            await WaitForWorkspacePhaseAsync(
                control,
                "Ready",
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken).ConfigureAwait(false);
            DocumentDiagnosticReport ready = await lsp.RequestDiagnosticsAsync(
                documentPath,
                previousResultId: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "CS0103",
                ready.Items?.Select(static diagnostic => diagnostic.Code) ?? []);
            WorkspaceDiagnosticReport closedFiles = await lsp.RequestWorkspaceDiagnosticsAsync(
                [],
                partialResultToken: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(closedFiles.Items);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports C# and Razor findings with versions and incremental result identifiers.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceDiagnosticsTrackMultiProjectSnapshots()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string corePath = Path.Join(fixturePath, "Core");
            string webPath = Path.Join(fixturePath, "Web");
            string toolsPath = Path.Join(fixturePath, "Tools");
            Directory.CreateDirectory(corePath);
            Directory.CreateDirectory(webPath);
            Directory.CreateDirectory(toolsPath);
            string coreDocumentPath = Path.Join(corePath, "Broken.cs");
            string webDocumentPath = Path.Join(webPath, "Program.cs");
            string razorDocumentPath = Path.Join(webPath, "Component.razor");
            string toolsDocumentPath = Path.Join(toolsPath, "Tool.cs");
            await WriteFixtureAsync(
                fixturePath,
                coreDocumentPath,
                webDocumentPath,
                razorDocumentPath,
                toolsDocumentPath,
                TestContext.CancellationToken).ConfigureAwait(false);

            var client = new LspTestClient(
                legacyConfiguration: null,
                preferredConfiguration: null,
                dotNetConfiguration:
                    """
                    {
                      "backgroundAnalysis": {
                        "analyzerDiagnosticsScope": "fullSolution",
                        "compilerDiagnosticsScope": "openFiles"
                      }
                    }
                    """);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-workspace-diagnostic-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """
                {
                  "workspace": {"configuration": true},
                  "textDocument": {"diagnostic": {}}
                }
                """);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                capabilities.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(initialization
                .GetProperty("capabilities")
                .GetProperty("diagnosticProvider")
                .GetProperty("workspaceDiagnostics")
                .GetBoolean());
            await lsp.OpenDocumentAsync(coreDocumentPath, InvalidCoreText).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(razorDocumentPath, RazorText, "razor")
                .ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken,
                expectedProcessId: lsp.ProcessId).ConfigureAwait(false);

            WorkspaceDiagnosticReport initial = await lsp.RequestWorkspaceDiagnosticsAsync(
                [],
                partialResultToken: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Dictionary<string, WorkspaceDocumentDiagnosticReport> initialByPath =
                IndexByPath(initial);
            Assert.HasCount(4, initialByPath);
            WorkspaceDocumentDiagnosticReport coreReport = initialByPath[coreDocumentPath];
            Assert.AreEqual("full", coreReport.Kind);
            Assert.AreEqual(1, coreReport.Version);
            Assert.Contains("CS0103", GetCodes(coreReport));
            WorkspaceDocumentDiagnosticReport webReport = initialByPath[webDocumentPath];
            Assert.AreEqual("full", webReport.Kind);
            Assert.IsNull(webReport.Version);
            Assert.IsEmpty(GetCodes(webReport));
            WorkspaceDocumentDiagnosticReport razorReport = initialByPath[razorDocumentPath];
            Assert.AreEqual("full", razorReport.Kind);
            Assert.AreEqual(1, razorReport.Version);
            Assert.Contains("CS0103", GetCodes(razorReport));
            WorkspaceDocumentDiagnosticReport toolsReport = initialByPath[toolsDocumentPath];
            Assert.AreEqual("full", toolsReport.Kind);
            Assert.IsEmpty(GetCodes(toolsReport));

            using var tokenDocument = JsonDocument.Parse("42");
            WorkspaceDiagnosticReport partialResponse = await lsp
                .RequestWorkspaceDiagnosticsAsync(
                    CreatePreviousResults(initial),
                    tokenDocument.RootElement.Clone(),
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(partialResponse.Items);
            WorkspaceDiagnosticProgressParams progress = await client
                .ReadWorkspaceDiagnosticProgressAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(42, progress.Token.GetInt32());
            Assert.HasCount(4, progress.Value.Items);
            foreach (WorkspaceDocumentDiagnosticReport report in progress.Value.Items)
            {
                Assert.AreEqual("unchanged", report.Kind);
                Assert.IsNull(report.Items);
            }

            await lsp.ChangeDocumentAsync(
                coreDocumentPath,
                version: 2,
                [new TextDocumentContentChangeEvent { Text = ValidCoreText }])
                .ConfigureAwait(false);
            WorkspaceDiagnosticReport updated = await lsp.RequestWorkspaceDiagnosticsAsync(
                CreatePreviousResults(initial),
                partialResultToken: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Dictionary<string, WorkspaceDocumentDiagnosticReport> updatedByPath =
                IndexByPath(updated);
            WorkspaceDocumentDiagnosticReport updatedCoreReport = updatedByPath[coreDocumentPath];
            Assert.AreEqual("full", updatedCoreReport.Kind);
            Assert.AreEqual(2, updatedCoreReport.Version);
            Assert.DoesNotContain("CS0103", GetCodes(updatedCoreReport));
            Assert.Contains("CS0103", GetCodes(updatedByPath[razorDocumentPath]));
            Assert.AreNotEqual(coreReport.ResultId, updatedCoreReport.ResultId);
            WorkspaceDocumentDiagnosticReport updatedWebReport =
                updatedByPath[webDocumentPath];
            Assert.AreEqual("full", updatedWebReport.Kind);
            Assert.AreNotEqual(webReport.ResultId, updatedWebReport.ResultId);
            WorkspaceDocumentDiagnosticReport updatedToolsReport =
                updatedByPath[toolsDocumentPath];
            Assert.AreEqual("unchanged", updatedToolsReport.Kind);
            Assert.AreEqual(toolsReport.ResultId, updatedToolsReport.ResultId);
            Assert.IsNull(updatedToolsReport.Items);

            WorkspaceDiagnosticReport settled = await lsp.RequestWorkspaceDiagnosticsAsync(
                CreatePreviousResults(updated),
                partialResultToken: null,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.HasCount(4, settled.Items);
            foreach (WorkspaceDocumentDiagnosticReport report in settled.Items)
            {
                Assert.AreEqual("unchanged", report.Kind);
                Assert.IsNull(report.Items);
            }

            using var invalidTokenDocument = JsonDocument.Parse("true");
            RemoteInvocationException invalidToken =
                await Assert.ThrowsExactlyAsync<RemoteInvocationException>(
                    async () => await lsp.RequestWorkspaceDiagnosticsAsync(
                        CreatePreviousResults(updated),
                        invalidTokenDocument.RootElement.Clone(),
                        TestContext.CancellationToken).ConfigureAwait(false))
                    .ConfigureAwait(false);
            Assert.Contains(
                "must be an integer or string",
                invalidToken.Message,
                StringComparison.Ordinal);

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits a real workspace diagnostic pull into bounded progress notifications.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceDiagnosticsPublishBoundedPartialResults()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-workspace-diagnostic-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Fixture.csproj"),
                CoreProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await Task.WhenAll(Enumerable.Range(0, 129).Select(index =>
                File.WriteAllTextAsync(
                    Path.Join(fixturePath, $"Document{index}.cs"),
                    $"internal static class Document{index} {{ }}",
                    TestContext.CancellationToken))).ConfigureAwait(false);

            var client = new LspTestClient(
                legacyConfiguration: null,
                preferredConfiguration: null);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-workspace-diagnostic-progress-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath,
                client).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            using var capabilities = JsonDocument.Parse(
                """{"textDocument":{"diagnostic":{}}}""");
            using var initializationOptions = JsonDocument.Parse(
                """{"csls":{"diagnostics":{"workspace":true}}}""");
            await lsp.InitializeAsync(
                [fixturePath],
                capabilities.RootElement,
                initializationOptions.RootElement,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                fixturePath,
                TimeSpan.FromSeconds(30),
                TestContext.CancellationToken,
                expectedProcessId: lsp.ProcessId).ConfigureAwait(false);

            using var tokenDocument = JsonDocument.Parse("\"workspace\"");
            WorkspaceDiagnosticReport response = await lsp.RequestWorkspaceDiagnosticsAsync(
                [],
                tokenDocument.RootElement.Clone(),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsEmpty(response.Items);
            WorkspaceDiagnosticProgressParams first = await client
                .ReadWorkspaceDiagnosticProgressAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            WorkspaceDiagnosticProgressParams second = await client
                .ReadWorkspaceDiagnosticProgressAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("workspace", first.Token.GetString());
            Assert.AreEqual("workspace", second.Token.GetString());
            Assert.HasCount(128, first.Value.Items);
            Assert.HasCount(1, second.Value.Items);
            WorkspaceDocumentDiagnosticReport[] items =
                [.. first.Value.Items, .. second.Value.Items];
            Assert.HasCount(129, items);
            Assert.IsTrue(items.All(static item => item.Kind == "full"));
            Assert.IsTrue(items.All(static item => item.Version is null));

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(fixturePath, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, WorkspaceDocumentDiagnosticReport> IndexByPath(
        WorkspaceDiagnosticReport report) => report.Items.ToDictionary(
            static item => item.Uri.GetFileSystemPath(),
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

    private static IReadOnlyList<PreviousResultId> CreatePreviousResults(
        WorkspaceDiagnosticReport report) =>
        [.. report.Items.Select(static item => new PreviousResultId
        {
            Uri = item.Uri,
            Value = item.ResultId ?? throw new InvalidDataException(
                "A workspace diagnostic report had no result identifier.")
        })];

    private static string?[] GetCodes(WorkspaceDocumentDiagnosticReport report) =>
        report.Items?.Select(static diagnostic => diagnostic.Code).ToArray()
        ?? throw new InvalidDataException("A full workspace diagnostic report had no items.");

    private static async Task WaitForFileAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
        {
            if (File.Exists(path))
            {
                return;
            }
        }

        throw new InvalidOperationException("The file wait loop ended unexpectedly.");
    }

    private static async Task WaitForWorkspacePhaseAsync(
        ControlRpcClient control,
        string expectedPhase,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        while (await timer.WaitForNextTickAsync(timeoutSource.Token).ConfigureAwait(false))
        {
            ControlSessionInfo session = await control.GetSessionAsync(timeoutSource.Token)
                .ConfigureAwait(false);
            if (string.Equals(
                session.WorkspacePhase,
                expectedPhase,
                StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException("The workspace phase wait loop ended unexpectedly.");
    }

    private static async Task WriteFixtureAsync(
        string fixturePath,
        string coreDocumentPath,
        string webDocumentPath,
        string razorDocumentPath,
        string toolsDocumentPath,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Fixture.slnx"),
            SolutionText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Core", "Core.csproj"),
            CoreProjectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Web", "Web.csproj"),
            WebProjectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Tools", "Tools.csproj"),
            CoreProjectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            coreDocumentPath,
            InvalidCoreText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            webDocumentPath,
            WebDocumentText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            razorDocumentPath,
            RazorText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            toolsDocumentPath,
            ToolsDocumentText,
            cancellationToken).ConfigureAwait(false);
    }

    private const string SolutionText = """
        <Solution>
          <Project Path="Core/Core.csproj" />
          <Project Path="Tools/Tools.csproj" />
          <Project Path="Web/Web.csproj" />
        </Solution>
        """;

    private const string CoreProjectText = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
        </Project>
        """;

    private const string WebProjectText = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
          </PropertyGroup>
          <ItemGroup>
            <ProjectReference Include="../Core/Core.csproj" />
          </ItemGroup>
        </Project>
        """;

    private const string InvalidCoreText = """
        namespace Core;

        public static class Broken
        {
            public static void Run() => Console.WriteLine(MissingCore);
        }
        """;

    private const string ValidCoreText = """
        namespace Core;

        public static class Broken
        {
            public static void Run() => Console.WriteLine("fixed");
        }
        """;

    private const string WebDocumentText = """
        namespace Web;

        public static class Program
        {
            public static void Main() => Core.Broken.Run();
        }
        """;

    private const string RazorText = "<p>@MissingRazor</p>";

    private const string ToolsDocumentText = """
        namespace Tools;

        public static class Tool
        {
            public static int Value => 1;
        }
        """;
}

using Csls.Control.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies live-session selection, cancellation, and trace inspection through MCP.
/// </summary>
public sealed partial class McpLanguageServerTests
{
    /// <summary>
    /// Rejects ambiguous workspace selection and succeeds after the ambiguity is removed.
    /// </summary>
    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task WorkspaceSelectorRequiresOneUnambiguousLiveSession()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string mcpPath = Environment.GetEnvironmentVariable("CSLS_TEST_MCP_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Mcp",
                "debug",
                "csls-mcp.dll");
        string mcpWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_MCP_WORKER_PATH") ??
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Mcp.Worker",
                "debug",
                "csls-mcp-worker.dll");
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-ambiguous-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Ambiguous.csproj"),
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Join(fixturePath, "Program.cs"),
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            LspProcessSession first = await LspProcessSession.StartAsync(
                "csls-mcp-ambiguous-first",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                repositoryRoot).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable firstCleanup = first.ConfigureAwait(false);
            LspProcessSession second = await LspProcessSession.StartAsync(
                "csls-mcp-ambiguous-second",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                repositoryRoot).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable secondCleanup = second.ConfigureAwait(false);
            await first.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await second.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);

            string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
            Dictionary<string, string?> environment =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
            environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
            bool isManagedLauncher = string.Equals(
                Path.GetExtension(mcpPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase);
            List<string> arguments = [];
            if (isManagedLauncher)
            {
                arguments.Add(mcpPath);
            }

            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = isManagedLauncher ? dotnetHost : mcpPath,
                    Arguments = arguments,
                    Name = "csls-mcp-ambiguous-selection",
                    WorkingDirectory = repositoryRoot,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment,
                    StandardErrorLines = TestContext.WriteLine
                });
            McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            try
            {
                CallToolResult ambiguous = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?> { ["workspace"] = fixturePath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(ambiguous.IsError);
                Assert.IsNull(ambiguous.StructuredContent);
                Assert.Contains(
                    "Select a session by its process identifier",
                    ambiguous.Content.OfType<TextContentBlock>().Single().Text,
                    StringComparison.Ordinal);

                string secondDiagnostics = await second.ShutdownAsync(
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.DoesNotContain(
                    "Unhandled exception",
                    secondDiagnostics,
                    StringComparison.Ordinal);

                CallToolResult selected = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?> { ["workspace"] = fixturePath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(selected.IsError);
                ControlSessionInfo session = McpAssertions.GetStructuredContent(selected).Deserialize(
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException(
                        "MCP returned no session after workspace ambiguity was removed.");
                Assert.AreEqual(first.ProcessId, session.ProcessId);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            string firstDiagnostics = await first.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                firstDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                fixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Cancels and traces a live Roslyn analyzer request through official MCP client calls.
    /// </summary>
    [TestMethod]
    public async Task McpCancelsLiveAnalyzerRequestAndReturnsTrace()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string workerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string mcpPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Mcp",
            "debug",
            "csls-mcp.dll");
        string mcpWorkerPath = Path.Join(
            artifactsRoot,
            "bin",
            "Csls.Mcp.Worker",
            "debug",
            "csls-mcp-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(mcpPath), $"MCP launcher not found at {mcpPath}.");
        Assert.IsTrue(File.Exists(mcpWorkerPath), $"MCP worker not found at {mcpWorkerPath}.");
        string secondFixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-cancellation-second-{Guid.NewGuid():N}");
        Directory.CreateDirectory(secondFixturePath);
        try
        {
            string secondProjectPath = Path.Join(secondFixturePath, "Second.csproj");
            string secondDocumentPath = Path.Join(secondFixturePath, "Second.cs");
            await File.WriteAllTextAsync(
                secondProjectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                secondDocumentPath,
                "namespace Second; public static class Marker { }",
                TestContext.CancellationToken).ConfigureAwait(false);
            CancellationProbeFixture fixture = await CancellationProbeFixture.CreateAsync(
                repositoryRoot,
                TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
            LspProcessSession lsp = await LspProcessSession.StartAsync(
                "csls-mcp-cancellation-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                repositoryRoot).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            await lsp.InitializeAsync(
                fixture.RootPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await lsp.OpenDocumentAsync(
                fixture.DocumentPath,
                CancellationProbeFixture.DocumentText).ConfigureAwait(false);
            LspProcessSession secondLsp = await LspProcessSession.StartAsync(
                "csls-mcp-cancellation-second-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                repositoryRoot).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable secondLspCleanup =
                secondLsp.ConfigureAwait(false);
            await secondLsp.InitializeAsync(
                secondFixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            await secondLsp.CompleteInitializationAsync().ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                secondFixturePath,
                TimeSpan.FromSeconds(60),
                TestContext.CancellationToken,
                expectedProcessId: secondLsp.ProcessId).ConfigureAwait(false);

            string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
            Dictionary<string, string?> environment =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
            environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
            bool isManagedLauncher = string.Equals(
                Path.GetExtension(mcpPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase);
            List<string> arguments = [];
            if (isManagedLauncher)
            {
                arguments.Add(mcpPath);
            }

            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = isManagedLauncher ? dotnetHost : mcpPath,
                    Arguments = arguments,
                    Name = "csls-mcp-request-control",
                    WorkingDirectory = repositoryRoot,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment,
                    StandardErrorLines = TestContext.WriteLine
                });
            McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            try
            {
                IList<McpClientTool> tools = await client.ListToolsAsync(
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                McpClientTool listTool = tools.Single(static tool => tool.Name == "list_requests");
                McpClientTool cancelTool = tools.Single(static tool => tool.Name == "cancel_request");
                McpClientTool startTool = tools.Single(static tool => tool.Name == "start_trace");
                McpClientTool stopTool = tools.Single(static tool => tool.Name == "stop_trace");
                Assert.IsNotNull(listTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(cancelTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(startTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(stopTool.ProtocolTool.OutputSchema);
                ToolAnnotations listAnnotations = listTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException("The request list tool has no annotations.");
                Assert.IsTrue(listAnnotations.ReadOnlyHint);
                Assert.IsFalse(listAnnotations.DestructiveHint);
                Assert.IsTrue(listAnnotations.IdempotentHint);
                Assert.IsFalse(listAnnotations.OpenWorldHint);
                ToolAnnotations cancelAnnotations = cancelTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException("The request cancellation tool has no annotations.");
                Assert.IsFalse(cancelAnnotations.ReadOnlyHint);
                Assert.IsTrue(cancelAnnotations.DestructiveHint);
                Assert.IsTrue(cancelAnnotations.IdempotentHint);
                Assert.IsFalse(cancelAnnotations.OpenWorldHint);
                ToolAnnotations startAnnotations = startTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException("The trace start tool has no annotations.");
                Assert.IsFalse(startAnnotations.ReadOnlyHint);
                Assert.IsFalse(startAnnotations.DestructiveHint);
                Assert.IsFalse(startAnnotations.IdempotentHint);
                Assert.IsFalse(startAnnotations.OpenWorldHint);
                ToolAnnotations stopAnnotations = stopTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException("The trace stop tool has no annotations.");
                Assert.IsFalse(stopAnnotations.ReadOnlyHint);
                Assert.IsFalse(stopAnnotations.DestructiveHint);
                Assert.IsFalse(stopAnnotations.IdempotentHint);
                Assert.IsFalse(stopAnnotations.OpenWorldHint);

                CallToolResult invalidCancellation = await client.CallToolAsync(
                    "cancel_request",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["correlationId"] = "not-a-correlation-id"
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsTrue(invalidCancellation.IsError);
                Assert.IsNull(invalidCancellation.StructuredContent);

                CallToolResult startResult = await client.CallToolAsync(
                    "start_trace",
                    new Dictionary<string, object?> { ["session"] = lsp.ProcessId },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(startResult.IsError);
                ControlTraceInfo startedTrace = McpAssertions.GetStructuredContent(startResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlTraceInfo)
                    ?? throw new InvalidDataException("MCP returned no started trace value.");
                Assert.IsTrue(startedTrace.IsActive);
                Assert.IsNotNull(startedTrace.TraceId);

                Task<CallToolResult> diagnosticRequest = client.CallToolAsync(
                    "get_diagnostics",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["documentPath"] = fixture.DocumentPath
                    },
                    cancellationToken: TestContext.CancellationToken).AsTask();
                await FileTextWaiter.WaitAsync(
                    fixture.MarkerPath,
                    "started",
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
                CallToolResult listResult = await client.CallToolAsync(
                    "list_requests",
                    new Dictionary<string, object?> { ["session"] = lsp.ProcessId },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(listResult.IsError);
                ControlRequestSchedulerInfo requests = McpAssertions.GetStructuredContent(listResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlRequestSchedulerInfo)
                    ?? throw new InvalidDataException("MCP returned no request scheduler value.");
                ControlRequestInfo request = requests.ActiveRequests.Single(static item =>
                    item.Name == "textDocument/diagnostic");
                Assert.AreEqual("Running", request.Status);
                Assert.IsTrue(requests.Trace.IsActive);

                CallToolResult cancelResult = await client.CallToolAsync(
                    "cancel_request",
                    new Dictionary<string, object?>
                    {
                        ["session"] = lsp.ProcessId,
                        ["correlationId"] = request.CorrelationId.ToString("D")
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(cancelResult.IsError);
                ControlCancelRequestResult cancellation = McpAssertions.GetStructuredContent(cancelResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlCancelRequestResult)
                    ?? throw new InvalidDataException("MCP returned no request cancellation value.");
                Assert.AreEqual(request.CorrelationId, cancellation.CorrelationId);
                Assert.IsTrue(cancellation.CancellationRequested);
                CallToolResult diagnosticResult = await diagnosticRequest.ConfigureAwait(false);
                Assert.IsTrue(diagnosticResult.IsError);
                await FileTextWaiter.WaitAsync(
                    fixture.MarkerPath,
                    "canceled",
                    TimeSpan.FromSeconds(60),
                    TestContext.CancellationToken).ConfigureAwait(false);
                string marker = await File.ReadAllTextAsync(
                    fixture.MarkerPath,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.Contains("canceled", marker, StringComparison.Ordinal);

                CallToolResult secondTargetResult = await client.CallToolAsync(
                    "get_session",
                    new Dictionary<string, object?> { ["session"] = secondLsp.ProcessId },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(secondTargetResult.IsError);
                ControlSessionInfo secondTarget = McpAssertions.GetStructuredContent(secondTargetResult)
                    .Deserialize(ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException(
                        "MCP returned no second session after analyzer cancellation.");
                Assert.AreEqual(secondLsp.ProcessId, secondTarget.ProcessId);
                Assert.AreEqual(
                    secondFixturePath,
                    Assert.ContainsSingle(secondTarget.WorkspaceRoots));

                CallToolResult stopResult = await client.CallToolAsync(
                    "stop_trace",
                    new Dictionary<string, object?> { ["session"] = lsp.ProcessId },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(stopResult.IsError);
                ControlTraceInfo stoppedTrace = McpAssertions.GetStructuredContent(stopResult).Deserialize(
                    ControlJsonSerializerContext.Default.ControlTraceInfo)
                    ?? throw new InvalidDataException("MCP returned no stopped trace value.");
                Assert.IsFalse(stoppedTrace.IsActive);
                Assert.AreEqual(startedTrace.TraceId, stoppedTrace.TraceId);
                ControlTraceEntry entry = stoppedTrace.Entries.Single(item =>
                    item.CorrelationId == request.CorrelationId);
                Assert.AreEqual("Canceled", entry.Status);
                Assert.AreEqual(request.WorkspaceGeneration, entry.WorkspaceGeneration);
                Assert.IsTrue(entry.IsCancellationRequested);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            string diagnostics = await lsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain("Unhandled exception", diagnostics, StringComparison.Ordinal);
            string secondDiagnostics = await secondLsp.ShutdownAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.DoesNotContain(
                "Unhandled exception",
                secondDiagnostics,
                StringComparison.Ordinal);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                secondFixturePath,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static async Task<ControlWorkspaceOperationResult> CallWorkspaceOperationAsync(
        McpClient client,
        int processId,
        string toolName,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            toolName,
            new Dictionary<string, object?> { ["session"] = processId },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsNull(result.IsError);
        return McpAssertions.GetStructuredContent(result).Deserialize(
            ControlJsonSerializerContext.Default.ControlWorkspaceOperationResult)
            ?? throw new InvalidDataException(
                $"MCP returned no workspace operation result for {toolName}.");
    }

    private static JsonElement GetStructuredCollection(
        CallToolResult result,
        string negotiatedProtocolVersion)
    {
        Assert.IsNull(result.IsError);
        JsonElement structuredContent = McpAssertions.GetStructuredContent(result);
        if (string.CompareOrdinal(
                negotiatedProtocolVersion,
                NaturalStructuredOutputProtocolVersion) >= 0)
        {
            Assert.AreEqual(JsonValueKind.Array, structuredContent.ValueKind);
            return structuredContent;
        }

        Assert.AreEqual(JsonValueKind.Object, structuredContent.ValueKind);
        Assert.IsTrue(structuredContent.TryGetProperty("result", out JsonElement collection));
        Assert.AreEqual(JsonValueKind.Array, collection.ValueKind);
        return collection;
    }

    private const string NaturalStructuredOutputProtocolVersion = "2026-07-28";

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
                Console.WriteLine(Missing);
                Helper(1);
            }

            private static void Helper(int value)
            {
                Console.WriteLine( value );
            }
        }
        """;

    private const string ImportsText = """
        using System.Text;
        using System;

        namespace Fixture;

        public static class Imports;
        """;

    private const string FormattingText = """
        namespace Fixture;

        public static class Formatting{public static int Add(int left,int right)=>left+right;}
        """;

    private const string MissingUsingText = """
        namespace Fixture;

        public static class MissingUsing
        {
            public static string Build()
            {
                var builder = new StringBuilder();
                return builder.ToString();
            }
        }
        """;

    private const string ImplementInterfaceText = """
        namespace InterfaceActions;

        public interface IRunner
        {
            string Run(int value);
        }

        public sealed class Runner : IRunner
        {
        }
        """;

    private const string AdvancedDocumentText = """
        namespace Fixture;

        public interface IRunner
        {
            void Execute();
        }

        public sealed class Runner : IRunner
        {
            public void Execute()
            {
            }
        }

        public static class AdvancedProgram
        {
            public static void Run()
            {
                IRunner runner = new Runner();
                runner.Execute();
                runner = new Runner();
                _ = runner;
            }
        }
        """;

    private const string MoveTypeDocumentText = """
        namespace Fixture;

        public static class MoveTypes
        {
            public static int Read() => McpHelper.Value;
        }

        internal static class McpHelper
        {
            public static int Value => 42;
        }
        """;
}

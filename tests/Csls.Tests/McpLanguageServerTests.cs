using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Csls.Control.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Csls.Tests;

/// <summary>
/// Verifies the official MCP C# SDK against a real csls worker and Unix-domain socket.
/// </summary>
[TestClass]
public sealed class McpLanguageServerTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Lists and invokes real tools, resources, and prompts through MCP standard input and output.
    /// </summary>
    [TestMethod]
    public async Task McpExposesAttachedLanguageServerSession()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        string mcpPath = Environment.GetEnvironmentVariable("CSLS_TEST_MCP_PATH") ??
            Path.Combine(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Mcp",
                "debug",
                "csls-mcp.dll");
        string mcpWorkerPath =
            Environment.GetEnvironmentVariable("CSLS_TEST_MCP_WORKER_PATH") ??
            Path.Combine(
                repositoryRoot,
                "artifacts",
                "bin",
                "Csls.Mcp.Worker",
                "debug",
                "csls-mcp-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        Assert.IsTrue(File.Exists(mcpPath), $"MCP launcher not found at {mcpPath}.");
        Assert.IsTrue(
            File.Exists(mcpWorkerPath),
            $"MCP worker not found at {mcpWorkerPath}.");

        string fixturePath = Path.Combine(
            Path.GetTempPath(),
            $"csls-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        try
        {
            string projectPath = Path.Combine(fixturePath, "Fixture.csproj");
            string documentPath = Path.Combine(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            var lsp = LspProcessSession.Start(
                "csls-mcp-worker",
                EditorToolResolver.ResolveDotNetHost(),
                [workerPath],
                fixturePath);
            await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
            JsonElement initialization = await lsp.InitializeAsync(
                fixturePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "csls",
                initialization.GetProperty("serverInfo").GetProperty("name").GetString());
            await lsp.OpenDocumentAsync(documentPath, DocumentText).ConfigureAwait(false);

            string dotnetHost = EditorToolResolver.ResolveDotNetHost();
            Dictionary<string, string?> environment =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetHost);
            environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
            bool isManagedLauncher = string.Equals(
                Path.GetExtension(mcpPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase);
            List<string> mcpArguments = [];
            if (isManagedLauncher)
            {
                mcpArguments.Add(mcpPath);
            }

            mcpArguments.Add("--session");
            mcpArguments.Add(lsp.ProcessId.ToString(CultureInfo.InvariantCulture));
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = isManagedLauncher ? dotnetHost : mcpPath,
                    Arguments = mcpArguments,
                    Name = "csls-mcp-integration",
                    WorkingDirectory = fixturePath,
                    InheritEnvironmentVariables = false,
                    EnvironmentVariables = environment,
                    StandardErrorLines = TestContext.WriteLine
                });
            McpClient client = await McpClient.CreateAsync(
                transport,
                cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
            try
            {
                Assert.AreEqual("csls", client.ServerInfo.Name);
                Assert.IsNotNull(client.NegotiatedProtocolVersion);

                IList<McpClientTool> tools = await client
                    .ListToolsAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                McpClientTool sessionTool = tools.Single(static tool =>
                    tool.Name == "get_session");
                McpClientTool hoverTool = tools.Single(static tool =>
                    tool.Name == "get_hover");
                ToolAnnotations annotations = sessionTool.ProtocolTool.Annotations
                    ?? throw new InvalidDataException("The session tool has no MCP annotations.");
                Assert.IsNotNull(annotations.ReadOnlyHint);
                Assert.IsTrue(annotations.ReadOnlyHint.Value);
                Assert.IsNotNull(annotations.DestructiveHint);
                Assert.IsFalse(annotations.DestructiveHint.Value);
                Assert.IsNotNull(annotations.OpenWorldHint);
                Assert.IsFalse(annotations.OpenWorldHint.Value);
                Assert.IsNotNull(annotations.IdempotentHint);
                Assert.IsTrue(annotations.IdempotentHint.Value);
                Assert.IsNotNull(sessionTool.ProtocolTool.OutputSchema);
                Assert.IsNotNull(hoverTool.ProtocolTool.OutputSchema);

                CallToolResult sessionResult = await client.CallToolAsync(
                    "get_session",
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(sessionResult.IsError);
                Assert.IsTrue(sessionResult.StructuredContent.HasValue);
                ControlSessionInfo session = sessionResult.StructuredContent.Value.Deserialize(
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException("MCP returned no structured session value.");
                Assert.AreEqual(lsp.ProcessId, session.ProcessId);
                Assert.AreEqual("Running", session.LifecycleState);
                Assert.AreEqual(fixturePath, session.WorkspaceRoots.Single());

                CallToolResult hoverResult = await client.CallToolAsync(
                    "get_hover",
                    new Dictionary<string, object?>
                    {
                        ["documentPath"] = documentPath,
                        ["line"] = 6,
                        ["character"] = 10
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(hoverResult.IsError);
                Assert.IsTrue(hoverResult.StructuredContent.HasValue);
                ControlHoverResult hover = hoverResult.StructuredContent.Value.Deserialize(
                    ControlJsonSerializerContext.Default.ControlHoverResult)
                    ?? throw new InvalidDataException("MCP returned no structured hover value.");
                Assert.IsTrue(hover.Found);
                Assert.IsNotNull(hover.Hover);
                Assert.Contains("System.Console", hover.Hover.Contents.Value);

                IList<McpClientResource> resources = await client
                    .ListResourcesAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.Contains(
                    "csls://session/current",
                    resources.Select(static resource => resource.Uri));
                ReadResourceResult resourceResult = await client.ReadResourceAsync(
                    new Uri("csls://session/current", UriKind.Absolute),
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                TextResourceContents sessionResource = resourceResult.Contents
                    .OfType<TextResourceContents>()
                    .Single();
                ControlSessionInfo resourceSession = JsonSerializer.Deserialize(
                    sessionResource.Text,
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException("MCP returned no session resource value.");
                Assert.AreEqual(lsp.ProcessId, resourceSession.ProcessId);

                IList<McpClientPrompt> prompts = await client
                    .ListPromptsAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                IEnumerable<string> promptNames = prompts.Select(static prompt => prompt.Name);
                Assert.Contains("diagnose_csharp", promptNames);
                Assert.Contains("explain_symbol", promptNames);
                Assert.Contains("review_csharp", promptNames);
                Assert.Contains("refactor_csharp", promptNames);
                Assert.Contains("troubleshoot_csls", promptNames);
                GetPromptResult promptResult = await client.GetPromptAsync(
                    "diagnose_csharp",
                    new Dictionary<string, object?> { ["scope"] = documentPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNotEmpty(promptResult.Messages);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            string diagnostics = await lsp.ShutdownAsync(
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
}

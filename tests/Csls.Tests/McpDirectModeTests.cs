using Csls.Control;
using Csls.Control.Contracts;
using Csls.Protocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies MCP direct mode against a real transient csls language-server process.
/// </summary>
[TestClass]
public sealed class McpDirectModeTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Starts a project-scoped transient session and removes it when the MCP client disconnects.
    /// </summary>
    [TestMethod]
    public async Task WorkspaceModeOwnsTransientLanguageServerLifetime()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        string serverWorkerPath = Path.Join(
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
        Assert.IsTrue(File.Exists(serverWorkerPath));
        Assert.IsTrue(File.Exists(mcpPath));
        Assert.IsTrue(File.Exists(mcpWorkerPath));

        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        int transientProcessId = 0;
        try
        {
            string projectPath = Path.Join(fixturePath, "Direct.csproj");
            string documentPath = Path.Join(fixturePath, "Program.cs");
            await File.WriteAllTextAsync(
                projectPath,
                ProjectText,
                TestContext.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                documentPath,
                DocumentText,
                TestContext.CancellationToken).ConfigureAwait(false);

            string dotnetHost = EditorToolResolver.ResolveDotNetHost();
            Dictionary<string, string?> environment =
                StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetHost);
            environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
            environment["CSLS_SERVER_WORKER_PATH"] = serverWorkerPath;
            bool isManagedLauncher = string.Equals(
                Path.GetExtension(mcpPath),
                ".dll",
                StringComparison.OrdinalIgnoreCase);
            List<string> arguments = [];
            if (isManagedLauncher)
            {
                arguments.Add(mcpPath);
            }

            arguments.Add("--workspace");
            arguments.Add(projectPath);
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Command = isManagedLauncher ? dotnetHost : mcpPath,
                    Arguments = arguments,
                    Name = "csls-mcp-direct-integration",
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
                CallToolResult sessionResult = await client.CallToolAsync(
                    "get_session",
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(sessionResult.IsError);
                Assert.IsTrue(sessionResult.StructuredContent.HasValue);
                ControlSessionInfo session = sessionResult.StructuredContent.Value.Deserialize(
                    ControlJsonSerializerContext.Default.ControlSessionInfo)
                    ?? throw new InvalidDataException(
                        "MCP direct mode returned no session information.");
                transientProcessId = session.ProcessId;
                Assert.AreNotEqual(Environment.ProcessId, transientProcessId);
                Assert.AreEqual(projectPath, Assert.ContainsSingle(session.WorkspaceRoots));

                CallToolResult workspaceResult = await client.CallToolAsync(
                    "get_workspace_state",
                    new Dictionary<string, object?>
                    {
                        ["includeDiagnostics"] = true
                    },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.IsNull(workspaceResult.IsError);
                Assert.IsTrue(workspaceResult.StructuredContent.HasValue);
                ControlDashboardSnapshot workspace = workspaceResult.StructuredContent.Value
                    .Deserialize(ControlJsonSerializerContext.Default.ControlDashboardSnapshot)
                    ?? throw new InvalidDataException(
                        "MCP direct mode returned no workspace state.");
                Assert.Contains(
                    projectPath,
                    workspace.Projects.Select(static project => project.FilePath));
                Assert.Contains(
                    documentPath,
                    workspace.Documents.Select(static document => document.FilePath));

                ReadResourceResult diagnosticResource = await client.ReadResourceAsync(
                    "csls://diagnostic{?path}",
                    new Dictionary<string, object?> { ["path"] = documentPath },
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                DocumentDiagnosticReport diagnostics = JsonSerializer.Deserialize(
                    diagnosticResource.Contents.OfType<TextResourceContents>().Single().Text,
                    ControlJsonSerializerContext.Default.DocumentDiagnosticReport)
                    ?? throw new InvalidDataException(
                        "MCP direct mode returned no diagnostic resource.");
                Assert.Contains(
                    "CS0103",
                    diagnostics.Items?.Select(static diagnostic => diagnostic.Code) ?? []);
            }
            finally
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }

            await WaitForProcessExitAsync(
                transientProcessId,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(ControlEndpoint.GetSocketPath(transientProcessId)));
        }
        finally
        {
            if (transientProcessId > 0)
            {
                await WaitForProcessExitAsync(
                    transientProcessId,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            Directory.Delete(fixturePath, recursive: true);
        }
    }

    private async Task WaitForProcessExitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Task waitTask = process.WaitForExitAsync(cancellationToken);
            ValueTask waitCompletion = new(waitTask);
            await waitCompletion.ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            TestContext.WriteLine($"Process {processId} already exited.");
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
        Console.WriteLine(Missing);
        """;
}

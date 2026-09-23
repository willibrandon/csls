using Csls.Control.Contracts;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Security;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Provides shared process setup and protocol assertions for workspace-selection tests.
/// </summary>
public sealed partial class McpWorkspaceSelectionTests
{
    private static (string ServerWorkerPath, string McpPath, string McpWorkerPath)
        ResolveProductPaths(string repositoryRoot)
    {
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
        Assert.IsTrue(
            File.Exists(serverWorkerPath),
            $"Server worker not found at {serverWorkerPath}.");
        Assert.IsTrue(File.Exists(mcpPath), $"MCP launcher not found at {mcpPath}.");
        Assert.IsTrue(
            File.Exists(mcpWorkerPath),
            $"MCP worker not found at {mcpWorkerPath}.");
        return (serverWorkerPath, mcpPath, mcpWorkerPath);
    }

    private async Task<McpClient> CreateMcpClientAsync(
        string repositoryRoot,
        string mcpPath,
        string mcpWorkerPath,
        string name,
        string? serverWorkerPath,
        CancellationToken cancellationToken)
    {
        string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
        environment["DOTNET_HOST_PATH"] = dotnetHost;
        environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
        if (serverWorkerPath is not null)
        {
            environment["CSLS_SERVER_WORKER_PATH"] = serverWorkerPath;
        }

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
                Name = name,
                WorkingDirectory = repositoryRoot,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                StandardErrorLines = TestContext.WriteLine
            });
        return await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LspProcessSession> StartLanguageServerAsync(
        string repositoryRoot,
        string serverWorkerPath,
        string workspacePath,
        string name,
        CancellationToken cancellationToken)
    {
        LspProcessSession lsp = await LspProcessSession.StartAsync(
            name,
            EditorToolResolver.ResolveDotNetHost(),
            [serverWorkerPath],
            repositoryRoot).ConfigureAwait(false);
        try
        {
            await lsp.InitializeAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            await lsp.CompleteInitializationAsync().ConfigureAwait(false);
            await ControlSessionWaiter.WaitForRunningAsync(
                workspacePath,
                TimeSpan.FromSeconds(60),
                cancellationToken).ConfigureAwait(false);
            return lsp;
        }
        catch
        {
            await lsp.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task DisconnectMcpAsync(
        McpProcessSession mcp,
        CancellationToken cancellationToken)
    {
        string diagnostics = await mcp.DisconnectAsync(
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);
        Assert.DoesNotContain(
            "Unhandled exception",
            diagnostics,
            StringComparison.Ordinal);
    }

    private static async Task WriteWorkspaceAsync(
        string projectPath,
        string documentPath,
        string documentText,
        CancellationToken cancellationToken)
    {
        const string projectText = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            projectPath,
            projectText,
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            documentPath,
            documentText,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task AssertToolErrorAsync(
        McpClient client,
        Dictionary<string, object?>? arguments,
        string expectedMessage,
        CancellationToken cancellationToken)
    {
        CallToolResult result = arguments is null
            ? await client.CallToolAsync(
                "get_session",
                cancellationToken: cancellationToken).ConfigureAwait(false)
            : await client.CallToolAsync(
                "get_session",
                arguments,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(result.IsError);
        Assert.IsNull(result.StructuredContent);
        Assert.Contains(
            expectedMessage,
            result.Content.OfType<TextContentBlock>().Single().Text,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AssertStartupArgumentRejectedAsync(
        string repositoryRoot,
        string mcpPath,
        string mcpWorkerPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        string dotnetHost = EditorToolResolver.ResolveAbsoluteDotNetHost();
        bool isManagedLauncher = string.Equals(
            Path.GetExtension(mcpPath),
            ".dll",
            StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isManagedLauncher ? dotnetHost : mcpPath,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            WorkingDirectory = repositoryRoot
        };
        if (isManagedLauncher)
        {
            startInfo.ArgumentList.Add(mcpPath);
        }

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_ROOT"] = EditorToolResolver.ResolveDotNetRoot();
        startInfo.Environment["DOTNET_HOST_PATH"] = dotnetHost;
        startInfo.Environment["CSLS_MCP_WORKER_PATH"] = mcpWorkerPath;
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The production MCP launcher did not start.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
        process.StandardInput.Close();
        try
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        string diagnostics = string.Concat(
            await outputTask.ConfigureAwait(false),
            Environment.NewLine,
            await errorTask.ConfigureAwait(false));
        Assert.AreNotEqual(0, process.ExitCode, diagnostics);
        Assert.Contains(arguments[0], diagnostics, StringComparison.Ordinal);
    }

    private static void AssertToolAnnotations(
        McpClientTool tool,
        bool readOnly,
        bool destructive,
        bool idempotent,
        bool openWorld)
    {
        ToolAnnotations annotations = tool.ProtocolTool.Annotations
            ?? throw new InvalidDataException(
                $"Tool {tool.Name} published no MCP behavior annotations.");
        Assert.AreEqual(readOnly, annotations.ReadOnlyHint, tool.Name);
        Assert.AreEqual(destructive, annotations.DestructiveHint, tool.Name);
        Assert.AreEqual(idempotent, annotations.IdempotentHint, tool.Name);
        Assert.AreEqual(openWorld, annotations.OpenWorldHint, tool.Name);
    }

    private static async Task AssertResourceErrorAsync(
        McpClient client,
        string uriTemplate,
        Dictionary<string, object?> variables,
        string expectedMessage,
        CancellationToken cancellationToken)
    {
        McpProtocolException exception = await Assert.ThrowsExactlyAsync<McpProtocolException>(
            async () => await client.ReadResourceAsync(
                uriTemplate,
                variables,
                cancellationToken: cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.Contains(
            expectedMessage,
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ControlSessionInfo> CallSessionAsync(
        McpClient client,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        CallToolResult result = await client.CallToolAsync(
            "get_session",
            arguments,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsNull(result.IsError);
        return McpAssertions.GetStructuredContent(result).Deserialize(
            ControlJsonSerializerContext.Default.ControlSessionInfo)
            ?? throw new InvalidDataException("MCP returned no selected session.");
    }

    private static string GetResourceText(ReadResourceResult result) =>
        result.Contents.OfType<TextResourceContents>().Single().Text;

    private static async Task AssertFileDeletedAsync(
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (File.Exists(path))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeoutSource.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"File was not deleted within {timeout}: {path}");
        }

        Assert.IsFalse(File.Exists(path));
    }

    private static string GetSchemaType(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out JsonElement type))
        {
            return type.ValueKind == JsonValueKind.String
                ? type.GetString() ?? string.Empty
                : type
                    .EnumerateArray()
                    .Select(static item => item.GetString())
                    .Single(static typeName => typeName != "null") ?? string.Empty;
        }

        return schema
            .GetProperty("anyOf")
            .EnumerateArray()
            .Select(static option => option.GetProperty("type").GetString())
            .Single(static typeName => typeName != "null") ?? string.Empty;
    }

    private static string CreateBlockedProjectText(
        string processHostPath,
        string buildStartedPath,
        string buildReleasePath)
    {
        string dotnetPath = EditorToolResolver.ResolveDotNetHost();
        string escapedDotnetPath = SecurityElement.Escape(dotnetPath)
            ?? throw new InvalidOperationException("The dotnet path could not be escaped.");
        string escapedProcessHostPath = SecurityElement.Escape(processHostPath)
            ?? throw new InvalidOperationException("The process-host path could not be escaped.");
        string escapedBuildStartedPath = SecurityElement.Escape(buildStartedPath)
            ?? throw new InvalidOperationException("The build marker path could not be escaped.");
        string escapedBuildReleasePath = SecurityElement.Escape(buildReleasePath)
            ?? throw new InvalidOperationException("The build release path could not be escaped.");
        return $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <Target Name="BlockDesignTimeBuild"
                      BeforeTargets="Compile"
                      Condition="'$(DesignTimeBuild)' == 'true'">
                <WriteLinesToFile File="{{escapedBuildStartedPath}}"
                                  Lines="started"
                                  Overwrite="true" />
                <Exec Command="&quot;{{escapedDotnetPath}}&quot; &quot;{{escapedProcessHostPath}}&quot; --wait-for-file &quot;{{escapedBuildReleasePath}}&quot;" />
              </Target>
            </Project>
            """;
    }
}

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csls.Support;

/// <summary>
/// Verifies MCP protocol behavior through the installed tool packages.
/// </summary>
internal static class ToolPackageMcpVerification
{
    private const double ProcessTimeoutMinutes = 3;

    /// <summary>
    /// Exercises initialization, workspace reuse, and shutdown through an installed MCP server.
    /// </summary>
    internal static async Task VerifyMcpWorkerAsync(
        string commandPath,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        IReadOnlyList<string>? commandArguments = null)
    {
        string fixturePath = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(fixturePath);
        string projectPath = Path.Join(fixturePath, "McpFixture.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Join(fixturePath, "Program.cs"),
            """Console.WriteLine("csls MCP package verification");""").ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(ProcessTimeoutMinutes));
        var startInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in commandArguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string value) in environment)
        {
            startInfo.Environment[name] = value;
        }
        startInfo.Environment.Remove("CSLS_MCP_WORKER_PATH");
        startInfo.Environment.Remove("CSLS_SERVER_WORKER_PATH");
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException(
            "The installed csls MCP server did not start.");
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteLineAsync(
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"csls-package-verifier","version":"1.0"}}}
                """).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            string initializeText = await process.StandardOutput
                .ReadLineAsync(timeout.Token)
                .ConfigureAwait(false) ?? throw new EndOfStreamException(
                    "The installed MCP server returned no initialize response.");
            using var initialize = JsonDocument.Parse(initializeText);
            if (!initialize.RootElement.TryGetProperty("result", out JsonElement result))
            {
                throw new InvalidDataException(
                    $"The installed MCP server rejected initialization: " +
                    initialize.RootElement.GetRawText());
            }

            string? serverName = result
                .GetProperty("serverInfo")
                .GetProperty("name")
                .GetString();
            if (!string.Equals(serverName, "csls", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The installed MCP server initialized as '{serverName}'.");
            }

            await process.StandardInput.WriteLineAsync(
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""")
                .ConfigureAwait(false);
            JsonElement sessionListResponse = await CallMcpToolAsync(
                process,
                requestId: 2,
                "list_sessions",
                [],
                timeout.Token).ConfigureAwait(false);
            if (!sessionListResponse.TryGetProperty("result", out _))
            {
                throw new InvalidDataException(
                    "The installed MCP worker did not process the session-list tool call.");
            }

            var targetArguments = new JsonObject { ["workspace"] = projectPath };
            JsonElement firstSessionResponse = await CallMcpToolAsync(
                process,
                requestId: 3,
                "get_session",
                targetArguments,
                timeout.Token).ConfigureAwait(false);
            JsonElement firstSession = GetStructuredToolResult(
                firstSessionResponse,
                "the first workspace selection");
            int transientProcessId = firstSession.GetProperty("processId").GetInt32();
            string socketPath = firstSession.GetProperty("socketPath").GetString()
                ?? throw new InvalidDataException(
                    "The installed MCP worker returned no transient socket path.");
            string selectedRoot = firstSession
                .GetProperty("workspaceRoots")
                .EnumerateArray()
                .Single()
                .GetString() ?? string.Empty;
            if (transientProcessId <= 0 ||
                !string.Equals(
                    Path.GetFullPath(selectedRoot),
                    Path.GetFullPath(projectPath),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal) ||
                !File.Exists(socketPath))
            {
                throw new InvalidDataException(
                    "The installed MCP worker did not start a live targeted workspace session.");
            }

            var workspaceArguments = new JsonObject { ["workspace"] = fixturePath };
            JsonElement workspaceResponse = await CallMcpToolAsync(
                process,
                requestId: 4,
                "get_workspace_state",
                workspaceArguments,
                timeout.Token).ConfigureAwait(false);
            JsonElement workspace = GetStructuredToolResult(
                workspaceResponse,
                "the repeated workspace selection");
            JsonElement workspaceCallResult = workspaceResponse.GetProperty("result");
            bool hasDetailsResourceLink = workspaceCallResult
                .GetProperty("content")
                .EnumerateArray()
                .Any(content =>
                    content.TryGetProperty("type", out JsonElement type) &&
                    string.Equals(type.GetString(), "resource_link", StringComparison.Ordinal) &&
                    content.TryGetProperty("uri", out JsonElement uri) &&
                    string.Equals(
                        uri.GetString(),
                        $"csls://workspace/?session={transientProcessId}",
                        StringComparison.Ordinal));
            int reusedProcessId = workspace.GetProperty("processId").GetInt32();
            int projectCount = workspace.GetProperty("projectCount").GetInt32();
            string expectedDetailsUri = $"csls://workspace/?session={transientProcessId}";
            string? detailsUri = workspace.GetProperty("detailsUri").GetString();
            if (reusedProcessId != transientProcessId ||
                projectCount < 1 ||
                !string.Equals(detailsUri, expectedDetailsUri, StringComparison.Ordinal) ||
                !hasDetailsResourceLink ||
                workspace.GetRawText().Length > 2_048)
            {
                throw new InvalidDataException(
                    "The installed MCP worker did not return a bounded overview for its reused " +
                    "targeted workspace session.");
            }

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await WaitForProcessExitAsync(transientProcessId, timeout.Token).ConfigureAwait(false);
            string standardError = await standardErrorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The installed MCP server exited with {process.ExitCode}: {standardError}");
            }

            if (File.Exists(socketPath))
            {
                throw new InvalidDataException(
                    "The installed MCP worker left its transient control socket after disconnect.");
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            InvalidOperationException or
            JsonException or
            KeyNotFoundException or
            OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            string standardError = await standardErrorTask.ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Installed MCP protocol verification failed: {exception.Message}" +
                $"{Environment.NewLine}{standardError}",
                exception);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (Directory.Exists(fixturePath))
            {
                Directory.Delete(fixturePath, recursive: true);
            }
        }
    }

    /// <summary>
    /// Extracts and exercises the framework-dependent MCP tool package.
    /// </summary>
    internal static async Task VerifyFrameworkDependentMcpWorkerAsync(
        string repositoryRoot,
        string verificationRoot,
        string packageRoot,
        string packageId,
        string commandName,
        string version)
    {
        string extractionPath = Path.Join(
            verificationRoot,
            "framework-dependent",
            packageId);
        Directory.CreateDirectory(extractionPath);
        await ZipFile.ExtractToDirectoryAsync(
            Path.Join(packageRoot, $"{packageId}.any.{version}.nupkg"),
            extractionPath,
            overwriteFiles: true,
            CancellationToken.None).ConfigureAwait(false);
        string commandAssembly = Path.Join(
            extractionPath,
            "tools",
            "net10.0",
            "any",
            commandName + ".dll");
        if (!File.Exists(commandAssembly))
        {
            throw new InvalidDataException(
                $"The framework-dependent {packageId} package omitted {commandAssembly}.");
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_HOME"] = Path.Join(
                verificationRoot,
                "dotnet-home",
                packageId + "-framework-dependent"),
            ["DOTNET_NOLOGO"] = "1"
        };
        await VerifyMcpWorkerAsync(
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            repositoryRoot,
            environment,
            [commandAssembly]).ConfigureAwait(false);
    }

    private static async Task<JsonElement> CallMcpToolAsync(
        Process process,
        int requestId,
        string toolName,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
            ["method"] = "tools/call",
            ["params"] = new JsonObject
            {
                ["name"] = toolName,
                ["arguments"] = arguments.DeepClone()
            }
        };
        await process.StandardInput.WriteLineAsync(request.ToJsonString()).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        while (true)
        {
            string responseText = await process.StandardOutput
                .ReadLineAsync(cancellationToken)
                .ConfigureAwait(false) ?? throw new EndOfStreamException(
                    $"The installed MCP server returned no response for request {requestId}.");
            using var response = JsonDocument.Parse(responseText);
            JsonElement root = response.RootElement;
            if (root.TryGetProperty("id", out JsonElement id) && id.GetInt32() == requestId)
            {
                return root.Clone();
            }
        }
    }

    private static JsonElement GetStructuredToolResult(JsonElement response, string operation)
    {
        if (!response.TryGetProperty("result", out JsonElement result) ||
            result.TryGetProperty("isError", out JsonElement isError) && isError.GetBoolean() ||
            !result.TryGetProperty("structuredContent", out JsonElement structuredContent))
        {
            throw new InvalidDataException(
                $"The installed MCP worker returned no structured content for {operation}: " +
                response.GetRawText());
        }

        return structuredContent;
    }

    private static async Task WaitForProcessExitAsync(
        int processId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }
}

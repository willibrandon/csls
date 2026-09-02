using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Csls.EndToEndPerformance;

/// <summary>
/// Measures the published MCP tool through the official C# client SDK.
/// </summary>
internal static class McpMeasurementClient
{
    /// <summary>
    /// Connects to a live session and invokes one real MCP tool.
    /// </summary>
    /// <param name="mcpServerPath">The published csls-mcp executable path.</param>
    /// <param name="languageServerProcessId">The attached language-server process.</param>
    /// <param name="workingDirectory">The measured workspace directory.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>A task that completes after the MCP session closes.</returns>
    internal static async Task MeasureAsync(
        string mcpServerPath,
        int languageServerProcessId,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<string>();
        Dictionary<string, string?> environment =
            StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        environment["DOTNET_NOLOGO"] = "1";
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = mcpServerPath,
                Arguments = [],
                Name = "csls-end-to-end-performance",
                WorkingDirectory = workingDirectory,
                InheritEnvironmentVariables = true,
                EnvironmentVariables = environment,
                StandardErrorLines = diagnostics.Add
            });
        McpClient client = await McpClient.CreateAsync(
            transport,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            CallToolResult result = await client.CallToolAsync(
                "get_session",
                new Dictionary<string, object?>
                {
                    ["session"] = languageServerProcessId
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (result.IsError is true || !result.StructuredContent.HasValue)
            {
                throw new InvalidDataException(
                    $"The MCP session probe failed: {string.Join(Environment.NewLine, diagnostics)}");
            }
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

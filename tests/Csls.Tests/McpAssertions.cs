using ModelContextProtocol.Protocol;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Requires structured results from real MCP tool calls before inspecting their contents.
/// </summary>
internal static class McpAssertions
{
    /// <summary>
    /// Captures a tool result's structured content or fails the calling test.
    /// </summary>
    /// <param name="result">The response received from the MCP server.</param>
    /// <returns>The non-null structured response payload.</returns>
    internal static JsonElement GetStructuredContent(CallToolResult result) =>
        result.StructuredContent ?? throw new AssertFailedException("MCP returned no structured content.");
}

using ModelContextProtocol.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Csls.Mcp.Worker;

/// <summary>
/// Creates structured debugger successes and recoverable MCP tool errors.
/// </summary>
internal static class McpDebuggerToolResult
{
    /// <summary>
    /// Runs one debugger operation without promoting expected failures to server faults.
    /// </summary>
    /// <typeparam name="T">The structured success type.</typeparam>
    /// <param name="operation">The debugger operation.</param>
    /// <returns>The structured MCP tool result.</returns>
    internal static async Task<CallToolResult> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            T value = await operation().ConfigureAwait(false);
            JsonElement structuredContent = JsonSerializer.SerializeToElement(
                value,
                McpJsonSerializerContext.Default.Options);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = structuredContent.GetRawText() }],
                StructuredContent = structuredContent
            };
        }
        catch (McpDebuggerException exception)
        {
            var error = new McpDebuggerError(exception.Code, exception.Message);
            JsonElement serializedError = JsonSerializer.SerializeToElement(
                error,
                McpJsonSerializerContext.Default.McpDebuggerError);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = serializedError.GetRawText() }],
                IsError = true,
                Meta = new JsonObject { ["errorCode"] = error.Code }
            };
        }
    }
}

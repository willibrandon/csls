using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Csls.Tests;

/// <summary>
/// Verifies MCP server diagnostics across real protocol failures and process cleanup.
/// </summary>
[TestClass]
public sealed class McpProcessSessionTests
{
    /// <summary>
    /// Gets the framework-managed cancellation token for each process exchange.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Retains server diagnostics when a failed request is followed by explicit or automatic disconnect.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FailedRequestRetainsServerDiagnostics(bool explicitDisconnect)
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-diagnostics-");
        string log = Path.Join(directory.FullName, "server.log");
        try
        {
            using var output = new StreamWriter(new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
            McpProcessSession session = await McpProcessSession.StartAsync(
                repository,
                EditorToolResolver.ResolveBuiltAssembly(repository, "Csls.Mcp", "csls-mcp.dll"),
                EditorToolResolver.ResolveBuiltAssembly(repository, "Csls.Mcp.Worker", "csls-mcp-worker.dll"),
                serverWorkerPath: null,
                TestContext.CancellationToken,
                diagnosticOutput: output).ConfigureAwait(false);
            string? completedDiagnostics = null;
            await using (session.ConfigureAwait(false))
            {
                IList<McpClientTool> tools = await session.Client.ListToolsAsync(cancellationToken: TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.Contains("list_sessions", tools.Select(static tool => tool.Name));
                McpProtocolException error = await Assert.ThrowsExactlyAsync<McpProtocolException>(async () =>
                    await session.Client.CallToolAsync("csls_unknown_diagnostic_tool",
                        cancellationToken: TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.Contains("csls_unknown_diagnostic_tool", error.Message);
                IList<McpClientTool> remainingTools = await session.Client.ListToolsAsync(
                    cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreSequenceEqual(tools.Select(static tool => tool.Name), remainingTools.Select(static tool => tool.Name),
                    "A failed request must leave the MCP connection available for subsequent requests.");
                if (explicitDisconnect)
                {
                    completedDiagnostics = await session.DisconnectAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }
            }

            string captured = await File.ReadAllTextAsync(log, TestContext.CancellationToken).ConfigureAwait(false);
            TestContext.WriteLine(captured);
            Assert.Contains("csls_unknown_diagnostic_tool", captured);
            Assert.Contains("Application started.", captured);
            Assert.Contains("Application is shutting down", captured);
            if (explicitDisconnect)
            {
                Assert.AreEqual(completedDiagnostics, captured);
            }
            await session.DisposeAsync().ConfigureAwait(false);
            Assert.AreEqual(captured, await File.ReadAllTextAsync(log, TestContext.CancellationToken).ConfigureAwait(false),
                "Repeated cleanup must not duplicate server diagnostics.");
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory.FullName, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}

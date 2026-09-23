namespace Csls.Tests;

/// <summary>
/// Starts the real packaged-shape processes used by MCP debugger tests.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static async Task<McpProcessSession> StartMcpAsync(
        CancellationToken cancellationToken)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        return await McpProcessSession.StartAsync(
            repositoryRoot,
            EditorToolResolver.ResolveBuiltAssembly(repositoryRoot, "Csls.Mcp", "csls-mcp.dll"),
            EditorToolResolver.ResolveBuiltAssembly(
                repositoryRoot,
                "Csls.Mcp.Worker",
                "csls-mcp-worker.dll"),
            serverWorkerPath: null,
            cancellationToken,
            EditorToolResolver.ResolveDebuggerWorker(repositoryRoot),
            EditorToolResolver.ResolveBuiltAssembly(
                repositoryRoot,
                "Csls.Debugger.Dump.Worker",
                "csls-debugger-dump-worker.dll")).ConfigureAwait(false);
    }
}

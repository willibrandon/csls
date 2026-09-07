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
        string artifactsRoot = EditorToolResolver.ResolveArtifactsRoot(repositoryRoot);
        return await McpProcessSession.StartAsync(
            repositoryRoot,
            Path.Join(artifactsRoot, "bin", "Csls.Mcp", "debug", "csls-mcp.dll"),
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Mcp.Worker",
                "debug",
                "csls-mcp-worker.dll"),
            serverWorkerPath: null,
            cancellationToken,
            EditorToolResolver.ResolveDebuggerWorker(repositoryRoot),
            Path.Join(
                artifactsRoot,
                "bin",
                "Csls.Debugger.Dump.Worker",
                "debug",
                "csls-debugger-dump-worker.dll")).ConfigureAwait(false);
    }
}

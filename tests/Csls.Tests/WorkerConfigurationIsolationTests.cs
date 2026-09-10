using ModelContextProtocol.Client;
using System.Globalization;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies that protocol hosts own their configuration independently of the current workspace.
/// </summary>
[TestClass]
public sealed class WorkerConfigurationIsolationTests
{
    /// <summary>
    /// Gets the framework-managed cancellation token for each real host exchange.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Starts protocol workers beside application configuration and observes their operating-system watches.
    /// </summary>
    /// <param name="mcp">Whether to exercise the MCP worker instead of the language-server worker.</param>
    /// <param name="configuration">The workspace application configuration, or null for an absent file.</param>
    [TestMethod]
    [DataRow(false, null)]
    [DataRow(true, null)]
    [DataRow(false, "{")]
    [DataRow(true, "{")]
    [DataRow(false, "{\"Logging\":{\"LogLevel\":{\"Default\":\"None\"}}}")]
    [DataRow(true, "{\"Logging\":{\"LogLevel\":{\"Default\":\"None\"}}}")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WorkspaceConfigurationPreservesProtocolHost(bool mcp, string? configuration)
    {
        string repository = EditorToolResolver.FindRepositoryRoot();
        string directory = Directory.CreateTempSubdirectory("csls-host-configuration-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Join(directory, "application", "nested"));
            if (configuration is not null)
            {
                await File.WriteAllTextAsync(Path.Join(directory, "appsettings.json"), configuration,
                    TestContext.CancellationToken).ConfigureAwait(false);
            }

            string diagnostics;
            if (mcp)
            {
                string worker = EditorToolResolver.ResolveBuiltAssembly(repository, "Csls.Mcp.Worker", "csls-mcp-worker.dll");
                McpProcessSession session = await McpProcessSession.StartAsync(directory, worker, worker,
                    serverWorkerPath: null, TestContext.CancellationToken).ConfigureAwait(false);
                await using (session.ConfigureAwait(false))
                {
                    IList<McpClientTool> tools = await session.Client.ListToolsAsync(
                        cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);
                    Assert.Contains("list_sessions", tools.Select(static tool => tool.Name));
                    await AssertHostWatchOwnershipAsync(session.LauncherProcess.Id).ConfigureAwait(false);
                    diagnostics = await session.DisconnectAsync(TimeSpan.FromSeconds(10), TestContext.CancellationToken)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                string worker = EditorToolResolver.ResolveServerWorker(repository);
                LspProcessSession session = await LspProcessSession.StartAsync("configuration-isolation",
                    EditorToolResolver.ResolveDotNetHost(), [worker], directory).ConfigureAwait(false);
                await using (session.ConfigureAwait(false))
                {
                    JsonElement initialized = await session.InitializeAsync(directory, TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    Assert.IsTrue(initialized.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
                    await AssertHostWatchOwnershipAsync(session.ProcessId).ConfigureAwait(false);
                    diagnostics = await session.ShutdownAsync(TestContext.CancellationToken).ConfigureAwait(false);
                }
            }

            Assert.Contains("Application started.", diagnostics);
            Assert.Contains("Application is shutting down", diagnostics);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task AssertHostWatchOwnershipAsync(int processId)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string directory = Path.Join("/proc", processId.ToString(CultureInfo.InvariantCulture), "fdinfo");
        foreach (string descriptor in Directory.EnumerateFiles(directory))
        {
            string[] information;
            try
            {
                information = await File.ReadAllLinesAsync(descriptor, TestContext.CancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // A completed host operation can close its descriptor after enumeration.
                continue;
            }
            Assert.DoesNotContain(static line => line.StartsWith("inotify wd:", StringComparison.Ordinal), information,
                $"Protocol initialization allocated a filesystem watch in {descriptor}.");
        }
    }
}

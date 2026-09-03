using ModelContextProtocol.Client;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies debugger lifecycle tools through real MCP and debugger worker processes.
/// </summary>
[TestClass]
public sealed partial class McpDebuggerLifecycleTests
{
    private static readonly string[] s_debuggerToolNames =
    [
        "debug_session_start",
        "debug_session_attach",
        "debug_sessions_list",
        "debug_session_get",
        "debug_session_end",
        "debug_execution_control",
        "debug_threads_get",
        "debug_stack_get",
        "debug_scopes_get",
        "debug_variables_get",
        "debug_modules_get"
    ];

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Publishes structured lifecycle schemas with exact MCP behavior annotations.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerLifecycleToolsPublishStructuredContracts()
    {
        McpProcessSession mcp = await StartMcpAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable mcpCleanup = mcp.ConfigureAwait(false);
        IList<McpClientTool> tools = await mcp.Client.ListToolsAsync(
            cancellationToken: TestContext.CancellationToken).ConfigureAwait(false);

        foreach (string name in s_debuggerToolNames)
        {
            McpClientTool tool = Assert.ContainsSingle(
                tools.Where(candidate => candidate.Name == name));
            Assert.IsNotNull(tool.ProtocolTool.OutputSchema, name);
            Assert.IsNotNull(tool.ProtocolTool.Annotations, name);
        }

        AssertAnnotations(tools, "debug_sessions_list", true, false, true, false);
        AssertAnnotations(tools, "debug_session_get", true, false, true, false);
        AssertAnnotations(tools, "debug_session_start", false, true, false, true);
        AssertAnnotations(tools, "debug_session_attach", false, true, false, true);
        AssertAnnotations(tools, "debug_session_end", false, true, false, true);
        AssertAnnotations(tools, "debug_execution_control", false, true, false, true);
        AssertAnnotations(tools, "debug_threads_get", true, false, true, false);
        AssertAnnotations(tools, "debug_stack_get", true, false, true, false);
        AssertAnnotations(tools, "debug_scopes_get", true, false, true, false);
        AssertAnnotations(tools, "debug_variables_get", true, false, true, false);
        AssertAnnotations(tools, "debug_modules_get", true, false, true, false);
    }

    /// <summary>
    /// Launches, selects, ends, and disconnect-cleans real managed target processes.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task DebuggerSessionOwnsRealTargetAcrossMcpLifecycle()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerFixture.cs");
        int breakpointLine = (await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false))
            .Select(static (line, index) => (Text: line, Line: index + 1))
            .Single(static item => item.Text.Contains(
                "Thread.SpinWait(10_000);",
                StringComparison.Ordinal))
            .Line;
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseLifecycleAsync(
                repositoryRoot,
                sourcePath,
                breakpointLine,
                testDirectory,
                TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

}

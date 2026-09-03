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
        "debug_session_restart",
        "debug_session_end",
        "debug_execution_control",
        "debug_threads_get",
        "debug_stack_get",
        "debug_scopes_get",
        "debug_variables_get",
        "debug_evaluate",
        "debug_execute_expression",
        "debug_modules_get",
        "debug_breakpoints_get",
        "debug_source_breakpoints_set",
        "debug_function_breakpoints_set",
        "debug_instruction_breakpoints_set",
        "debug_exception_breakpoints_set",
        "debug_exception_get",
        "debug_source_get",
        "debug_memory_read",
        "debug_disassemble",
        "debug_step_targets_get",
        "debug_goto_targets_get",
        "debug_goto",
        "debug_output_get"
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
        IList<McpClientResourceTemplate> templates = await mcp.Client
            .ListResourceTemplatesAsync(cancellationToken: TestContext.CancellationToken)
            .ConfigureAwait(false);
        IList<McpClientPrompt> prompts = await mcp.Client.ListPromptsAsync(
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
        AssertAnnotations(tools, "debug_session_restart", false, true, false, true);
        AssertAnnotations(tools, "debug_session_end", false, true, false, true);
        AssertAnnotations(tools, "debug_execution_control", false, true, false, true);
        AssertAnnotations(tools, "debug_threads_get", true, false, true, false);
        AssertAnnotations(tools, "debug_stack_get", true, false, true, false);
        AssertAnnotations(tools, "debug_scopes_get", true, false, true, false);
        AssertAnnotations(tools, "debug_variables_get", true, false, true, false);
        AssertAnnotations(tools, "debug_evaluate", true, false, true, false);
        AssertAnnotations(tools, "debug_execute_expression", false, true, false, true);
        AssertAnnotations(tools, "debug_modules_get", true, false, true, false);
        AssertAnnotations(tools, "debug_breakpoints_get", true, false, true, false);
        AssertAnnotations(tools, "debug_source_breakpoints_set", false, true, true, true);
        AssertAnnotations(tools, "debug_function_breakpoints_set", false, true, true, true);
        AssertAnnotations(tools, "debug_instruction_breakpoints_set", false, true, true, true);
        AssertAnnotations(tools, "debug_exception_breakpoints_set", false, true, true, true);
        AssertAnnotations(tools, "debug_exception_get", true, false, true, false);
        AssertAnnotations(tools, "debug_source_get", true, false, true, false);
        AssertAnnotations(tools, "debug_memory_read", true, false, true, false);
        AssertAnnotations(tools, "debug_disassemble", true, false, true, false);
        AssertAnnotations(tools, "debug_step_targets_get", true, false, true, false);
        AssertAnnotations(tools, "debug_goto_targets_get", true, false, true, false);
        AssertAnnotations(tools, "debug_goto", false, true, false, true);
        AssertAnnotations(tools, "debug_output_get", true, false, true, false);
        string[] templateUris =
            [.. templates.Select(static template => template.UriTemplate)];
        Assert.Contains("csls://debug/session/{debugSession}", templateUris);
        Assert.Contains(
            "csls://debug/output/{debugSession}{?afterSequence,count}",
            templateUris);
        Assert.Contains("csls://debug/breakpoints/{debugSession}", templateUris);
        Assert.Contains(
            "csls://debug/threads/{debugSession}/{stopGeneration}",
            templateUris);
        Assert.Contains(
            "csls://debug/stack/{debugSession}/{stopGeneration}/{threadId}{?startFrame,levels}",
            templateUris);
        Assert.Contains(
            "csls://debug/scopes/{debugSession}/{stopGeneration}/{frameId}",
            templateUris);
        Assert.Contains(
            "csls://debug/variables/{debugSession}/{stopGeneration}/{variablesReference}{?start,count}",
            templateUris);
        Assert.Contains(
            "csls://debug/modules/{debugSession}{?startModule,moduleCount}",
            templateUris);
        Assert.Contains(
            "csls://debug/exception/{debugSession}/{stopGeneration}/{threadId}",
            templateUris);
        Assert.Contains(
            "csls://debug/source/{debugSession}/{stopGeneration}/{sourceReference}{?start,count}",
            templateUris);
        Assert.Contains(
            "csls://debug/memory/{debugSession}/{stopGeneration}{?memoryReference,offset,count}",
            templateUris);
        Assert.Contains(
            "csls://debug/disassembly/{debugSession}/{stopGeneration}" +
                "{?instructionReference,byteOffset,instructionOffset,instructionCount,resolveSymbols}",
            templateUris);
        string[] promptNames = [.. prompts.Select(static prompt => prompt.Name)];
        Assert.Contains("diagnose_dotnet_debugger_failure", promptNames);
        Assert.Contains("plan_dotnet_breakpoints", promptNames);
        Assert.Contains("explain_dotnet_debugger_state", promptNames);
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
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = sourceLines
            .Select(static (line, index) => (Text: line, Line: index + 1))
            .Single(static item => item.Text.Contains(
                "Thread.SpinWait(10_000);",
                StringComparison.Ordinal))
            .Line;
        int localLine = sourceLines
            .Select(static (line, index) => (Text: line, Line: index + 1))
            .Single(static item => item.Text.Contains(
                "int localNumber = number + 1;",
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
                localLine,
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

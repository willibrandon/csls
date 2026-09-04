using ModelContextProtocol.Client;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
        "debug_dump_open",
        "debug_agent_control_set",
        "debug_sessions_list",
        "debug_session_get",
        "debug_session_restart",
        "debug_session_end",
        "debug_execution_control",
        "debug_threads_get",
        "debug_stack_get",
        "debug_scopes_get",
        "debug_variables_get",
        "debug_variables_get_presented",
        "debug_evaluate",
        "debug_watches_get",
        "debug_execute_expression",
        "debug_variable_set",
        "debug_expression_set",
        "debug_hot_reload",
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
    private static readonly string[] s_authorizationRequiredArguments =
    [
        "debugSession",
        "enabled"
    ];
    private static readonly string[] s_watchExpressions =
    [
        "localNumber",
        "localObject.NextNumber()"
    ];
    private static readonly string[] s_singleWatchExpression = ["localNumber"];

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
        AssertAnnotations(tools, "debug_dump_open", false, false, false, false);
        AssertAnnotations(tools, "debug_agent_control_set", false, false, false, false);
        AssertAnnotations(tools, "debug_session_restart", false, true, false, true);
        AssertAnnotations(tools, "debug_session_end", false, true, false, true);
        AssertAnnotations(tools, "debug_execution_control", false, true, false, true);
        AssertAnnotations(tools, "debug_threads_get", true, false, true, false);
        AssertAnnotations(tools, "debug_stack_get", true, false, true, false);
        AssertAnnotations(tools, "debug_scopes_get", true, false, true, false);
        AssertAnnotations(tools, "debug_variables_get", true, false, true, false);
        AssertAnnotations(tools, "debug_variables_get_presented", false, true, false, true);
        AssertAnnotations(tools, "debug_evaluate", true, false, true, false);
        AssertAnnotations(tools, "debug_watches_get", true, false, true, false);
        AssertAnnotations(tools, "debug_execute_expression", false, true, false, true);
        AssertAnnotations(tools, "debug_variable_set", false, true, false, true);
        AssertAnnotations(tools, "debug_expression_set", false, true, false, true);
        AssertAnnotations(tools, "debug_hot_reload", false, true, false, true);
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
        JsonElement startProperties = tools.Single(
            static tool => tool.Name == "debug_session_start")
            .ProtocolTool.InputSchema.GetProperty("properties");
        Assert.IsFalse(startProperties.TryGetProperty("agentControl", out _));
        Assert.IsTrue(startProperties.TryGetProperty("sourceFileMap", out _));
        Assert.IsTrue(startProperties.TryGetProperty("enableHotReload", out _));
        JsonElement attachProperties = tools.Single(
            static tool => tool.Name == "debug_session_attach")
            .ProtocolTool.InputSchema.GetProperty("properties");
        Assert.IsFalse(attachProperties.TryGetProperty("agentControl", out _));
        Assert.IsTrue(attachProperties.TryGetProperty("sourceFileMap", out _));
        JsonElement dumpProperties = tools.Single(
            static tool => tool.Name == "debug_dump_open")
            .ProtocolTool.InputSchema.GetProperty("properties");
        Assert.IsTrue(dumpProperties.TryGetProperty("dumpPath", out _));
        Assert.IsFalse(dumpProperties.TryGetProperty("progress", out _));
        JsonElement authorizationSchema = tools.Single(
            static tool => tool.Name == "debug_agent_control_set")
            .ProtocolTool.InputSchema;
        JsonElement authorizationOutputProperties = tools.Single(
            static tool => tool.Name == "debug_agent_control_set")
            .ProtocolTool.OutputSchema!.Value.GetProperty("properties");
        Assert.IsTrue(
            authorizationOutputProperties.TryGetProperty(
                "agentControlExpiresAtUtc",
                out _));
        string[] requiredAuthorizationArguments =
        [
            .. authorizationSchema.GetProperty("required")
                .EnumerateArray()
                .Select(static element => element.GetString()!)
        ];
        Assert.AreSequenceEqual(
            s_authorizationRequiredArguments,
            requiredAuthorizationArguments);
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
            "csls://debug/watches/{debugSession}/{stopGeneration}/{frameId}{?expression}",
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
        Assert.Contains("triage_dotnet_dump", promptNames);
    }

    /// <summary>
    /// Rejects relative source mappings before any debugger worker is started.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerLifecycleToolsRejectRelativeSourceMappings()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        var sourceFileMap = new Dictionary<string, string>
        {
            ["relative/source"] = repositoryRoot
        };
        McpProcessSession mcp = await StartMcpAsync(
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        await AssertToolErrorAsync(
            mcp.Client,
            "debug_session_start",
            new Dictionary<string, object?>
            {
                ["program"] = EditorToolResolver.ResolveTestProcessHost(repositoryRoot),
                ["workingDirectory"] = repositoryRoot,
                ["sourceFileMap"] = sourceFileMap
            },
            "debugger_request_invalid",
            TestContext.CancellationToken).ConfigureAwait(false);
        await AssertToolErrorAsync(
            mcp.Client,
            "debug_session_attach",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["sourceFileMap"] = sourceFileMap
            },
            "debugger_request_invalid",
            TestContext.CancellationToken).ConfigureAwait(false);
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
                "Thread.Sleep(1);",
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

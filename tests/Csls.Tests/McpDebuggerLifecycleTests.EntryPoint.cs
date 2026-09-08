using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies entry-stop semantics through real MCP and debugger worker transports.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    private static readonly string[] s_entryPointArguments = ["--print-environment", "CSLS_ENTRY_RESULT"];

    /// <summary>
    /// Returns recoverable environment-file errors and keeps the MCP connection available for a corrected launch.
    /// </summary>
    /// <param name="path">The malformed path or file selected through the tool transport.</param>
    /// <param name="errorCode">The expected structured debugger error.</param>
    [TestMethod]
    [DataRow("", "debugger_request_invalid")]
    [DataRow("\0", "debugger_request_invalid")]
    [DataRow("missing.env", "debugger_request_invalid")]
    [DataRow(".env", "debugger_operation_failed")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpEnvironmentFileErrorsAllowCorrectedLaunch(string path, string errorCode)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-environment-");
        string environmentFile = Path.Join(directory.FullName, ".env");
        try
        {
            await File.WriteAllTextAsync(environmentFile, "INVALID ASSIGNMENT", TestContext.CancellationToken)
                .ConfigureAwait(false);
            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
            var arguments = new Dictionary<string, object?>
            {
                ["program"] = EditorToolResolver.ResolveTestProcessHost(EditorToolResolver.FindRepositoryRoot()),
                ["workingDirectory"] = directory.FullName,
                ["environmentFilePath"] = path,
                ["stopAtEntry"] = true
            };
            await AssertToolErrorAsync(mcp.Client, "debug_session_start", arguments, errorCode,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement sessions = await CallAsync(mcp.Client, "debug_sessions_list", [], TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(JsonValueKind.Object, sessions.ValueKind,
                $"Negotiated MCP {mcp.Client.NegotiatedProtocolVersion}: {sessions.GetRawText()}");
            Assert.IsEmpty(sessions.GetProperty("sessions").EnumerateArray());
            await File.WriteAllTextAsync(environmentFile, "VALUE=corrected", TestContext.CancellationToken)
                .ConfigureAwait(false);
            arguments["environmentFilePath"] = ".env";
            JsonElement started = await CallAsync(mcp.Client, "debug_session_start", arguments, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string debugSession = started.GetProperty("debugSession").GetString()!;
            ProcessExitObservation exit = ProcessExitWaiter.Observe(started.GetProperty("processId").GetInt32());
            JsonElement stopped = await WaitForStoppedAsync(mcp.Client, debugSession, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("entry", stopped.GetProperty("stopReason").GetString());
            JsonElement ended = await CallAsync(mcp.Client, "debug_session_end",
                new Dictionary<string, object?> { ["debugSession"] = debugSession }, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("terminated", ended.GetProperty("state").GetString());
            await ProcessExitWaiter.WaitAsync(exit, TimeSpan.FromSeconds(10), TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(environmentFile);
            directory.Delete();
        }
    }

    /// <summary>
    /// Stops at authored entry, preserves observation-only access, and rearms on authorized restart.
    /// </summary>
    /// <param name="restart">Whether to replace the stopped target before continuing.</param>
    /// <param name="environmentMode">Whether values come from explicit entries, a file, or explicit overrides of a file.</param>
    [TestMethod]
    [DataRow(false, "direct")]
    [DataRow(true, "direct")]
    [DataRow(false, "file")]
    [DataRow(true, "file")]
    [DataRow(false, "override")]
    [DataRow(true, "override")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task McpStopAtEntryPreservesAuthorizationAndRestart(bool restart, string environmentMode)
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("csls-mcp-entry-");
        string path = Path.Join(directory.FullName, ".env");
        try
        {
            await File.WriteAllTextAsync(path, "CSLS_ENTRY_RESULT=file-result", TestContext.CancellationToken)
                .ConfigureAwait(false);
            await AssertMcpEntryPointAsync(restart, environmentMode, path).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private async Task AssertMcpEntryPointAsync(bool restart, string environmentMode, string environmentFile)
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string sourcePath = Path.Join(repositoryRoot, "tests", "Csls.TestProcessHost", "Program.cs");
        string sourceText = await File.ReadAllTextAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        CompilationUnitSyntax syntax = CSharpSyntaxTree.ParseText(sourceText, cancellationToken: TestContext.CancellationToken)
            .GetCompilationUnitRoot(TestContext.CancellationToken);
        GlobalStatementSyntax firstStatement = syntax.Members.OfType<GlobalStatementSyntax>().First();
        int entryLine = firstStatement.GetFirstToken().GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
        Dictionary<string, string> environment = environmentMode == "file" ? [] :
            new() { ["CSLS_ENTRY_RESULT"] = "entry-result" };
        JsonElement started = await CallAsync(mcp.Client, "debug_session_start",
            new Dictionary<string, object?>
            {
                ["program"] = EditorToolResolver.ResolveTestProcessHost(repositoryRoot),
                ["workingDirectory"] = Path.GetDirectoryName(environmentFile),
                ["arguments"] = s_entryPointArguments,
                ["environmentFilePath"] = environmentMode == "direct" ? null : ".env",
                ["environment"] = environment,
                ["sourceFileMap"] = new Dictionary<string, string> { ["/_/"] = repositoryRoot },
                ["stopAtEntry"] = true
            }, TestContext.CancellationToken).ConfigureAwait(false);
        string debugSession = started.GetProperty("debugSession").GetString()
            ?? throw new AssertFailedException("The launch omitted its debugger identity.");
        int processId = started.GetProperty("processId").GetInt32();
        ProcessExitObservation targetExit = ProcessExitWaiter.Observe(processId);
        JsonElement stopped = await WaitForStoppedAsync(mcp.Client, debugSession, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("entry", stopped.GetProperty("stopReason").GetString());
        Assert.IsFalse(stopped.GetProperty("agentControl").GetBoolean());
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        JsonElement frame = await ReadMcpEntryFrameAsync(mcp, stopped, sourcePath, entryLine).ConfigureAwait(false);
        var execution = new Dictionary<string, object?>
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = generation,
            ["operation"] = "continue"
        };
        await AssertToolErrorAsync(mcp.Client, "debug_execution_control", execution,
            "debugger_control_denied", TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement output = await CallAsync(mcp.Client, "debug_output_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsEmpty(output.GetProperty("entries").EnumerateArray());
        _ = await GrantAgentControlAsync(mcp.Client, debugSession, durationSeconds: 60,
            TestContext.CancellationToken).ConfigureAwait(false);

        if (restart)
        {
            await File.WriteAllTextAsync(environmentFile, "CSLS_ENTRY_RESULT=restart-result", TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement restarted = await CallAsync(mcp.Client, "debug_session_restart",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = generation
                }, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(debugSession, restarted.GetProperty("debugSession").GetString());
            int replacementId = restarted.GetProperty("processId").GetInt32();
            Assert.AreNotEqual(processId, replacementId);
            await ProcessExitWaiter.WaitAsync(targetExit, TimeSpan.FromSeconds(10),
                TestContext.CancellationToken).ConfigureAwait(false);
            targetExit = ProcessExitWaiter.Observe(replacementId);
            stopped = await WaitForStoppedAsync(mcp.Client, debugSession, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("entry", stopped.GetProperty("stopReason").GetString());
            Assert.IsGreaterThan(generation, stopped.GetProperty("stopGeneration").GetInt64());
            await AssertToolErrorAsync(mcp.Client, "debug_scopes_get",
                new Dictionary<string, object?>
                {
                    ["debugSession"] = debugSession,
                    ["stopGeneration"] = generation,
                    ["frameId"] = frame.GetProperty("id").GetInt32()
                }, "debugger_stale_generation", TestContext.CancellationToken).ConfigureAwait(false);
            _ = await ReadMcpEntryFrameAsync(mcp, stopped, sourcePath, entryLine).ConfigureAwait(false);
            execution["stopGeneration"] = stopped.GetProperty("stopGeneration").GetInt64();
        }

        _ = await CallAsync(mcp.Client, "debug_execution_control", execution, TestContext.CancellationToken)
            .ConfigureAwait(false);
        await ProcessExitWaiter.WaitAsync(targetExit, TimeSpan.FromSeconds(10), TestContext.CancellationToken)
            .ConfigureAwait(false);
        output = await CallAsync(mcp.Client, "debug_output_get",
            new Dictionary<string, object?> { ["debugSession"] = debugSession },
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement entry = Assert.ContainsSingle(output.GetProperty("entries").EnumerateArray());
        string expectedOutput = environmentMode == "file" ? (restart ? "restart-result" : "file-result") : "entry-result";
        Assert.AreEqual(expectedOutput, entry.GetProperty("output").GetString());
        Assert.AreEqual("standardOutput", entry.GetProperty("category").GetString());
        string diagnostics = await mcp.DisconnectAsync(TimeSpan.FromSeconds(20), TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.DoesNotContain("fail:", diagnostics, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonElement> ReadMcpEntryFrameAsync(
        McpProcessSession mcp, JsonElement stopped, string sourcePath, int entryLine)
    {
        long generation = stopped.GetProperty("stopGeneration").GetInt64();
        JsonElement stack = await CallAsync(mcp.Client, "debug_stack_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = stopped.GetProperty("debugSession").GetString(),
                ["stopGeneration"] = generation,
                ["threadId"] = stopped.GetProperty("stoppedThreadId").GetInt32(),
                ["levels"] = 1
            }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, stack.GetProperty("stopGeneration").GetInt64());
        JsonElement frame = Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray());
        Assert.AreEqual(sourcePath, frame.GetProperty("source").GetProperty("path").GetString());
        Assert.AreEqual(entryLine, frame.GetProperty("line").GetInt32());
        var frameArguments = new Dictionary<string, object?>
        {
            ["debugSession"] = stopped.GetProperty("debugSession").GetString(),
            ["stopGeneration"] = generation,
            ["frameId"] = frame.GetProperty("id").GetInt32()
        };
        JsonElement scopes = await CallAsync(mcp.Client, "debug_scopes_get", frameArguments,
            TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement argumentsScope = Assert.ContainsSingle(scopes.GetProperty("scopes").EnumerateArray()
            .Where(scope => scope.GetProperty("name").GetString() == "Arguments"));
        JsonElement variables = await CallAsync(mcp.Client, "debug_variables_get",
            new Dictionary<string, object?>
            {
                ["debugSession"] = stopped.GetProperty("debugSession").GetString(),
                ["stopGeneration"] = generation,
                ["variablesReference"] = argumentsScope.GetProperty("variablesReference").GetInt32()
            }, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, variables.GetProperty("stopGeneration").GetInt64());
        JsonElement argument = Assert.ContainsSingle(variables.GetProperty("variables").EnumerateArray());
        Assert.AreEqual("args", argument.GetProperty("name").GetString());
        Assert.AreEqual("string[]", argument.GetProperty("type").GetString());
        Assert.AreEqual("args", argument.GetProperty("evaluateName").GetString());
        frameArguments["expression"] = "args[1]";
        JsonElement evaluation = await CallAsync(mcp.Client, "debug_evaluate", frameArguments,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(generation, evaluation.GetProperty("stopGeneration").GetInt64());
        Assert.AreEqual("\"CSLS_ENTRY_RESULT\"", evaluation.GetProperty("evaluation").GetProperty("result").GetString());
        return frame;
    }
}

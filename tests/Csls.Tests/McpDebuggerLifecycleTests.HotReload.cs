using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Tests;

/// <summary>
/// Verifies bounded and explicitly authorized MCP Hot Reload mutation requests.
/// </summary>
public sealed partial class McpDebuggerLifecycleTests
{
    /// <summary>
    /// Rejects Hot Reload without control and malformed compiler payloads with control.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    public async Task DebuggerHotReloadRequiresControlAndValidCompilerDeltas()
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
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Single(static item => item.Line.Contains(
                "Thread.Sleep(1);",
                StringComparison.Ordinal))
            .Number;
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-mcp-hotreload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            McpProcessSession mcp = await StartMcpAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = mcp.ConfigureAwait(false);
            JsonElement started = await StartTargetAsync(
                mcp.Client,
                repositoryRoot,
                sourcePath,
                breakpointLine,
                Path.Join(testDirectory, "continue.signal"),
                TestContext.CancellationToken).ConfigureAwait(false);
            string debugSession = started.GetProperty("debugSession").GetString()!;
            JsonElement stopped = await WaitForStoppedAsync(
                mcp.Client,
                debugSession,
                TestContext.CancellationToken).ConfigureAwait(false);
            long generation = stopped.GetProperty("stopGeneration").GetInt64();
            await AssertToolErrorAsync(
                mcp.Client,
                "debug_hot_reload",
                CreateHotReloadArguments(debugSession, generation, "AA=="),
                "debugger_control_denied",
                TestContext.CancellationToken).ConfigureAwait(false);

            _ = await GrantAgentControlAsync(
                mcp.Client,
                debugSession,
                durationSeconds: 60,
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertToolErrorAsync(
                mcp.Client,
                "debug_hot_reload",
                CreateHotReloadArguments(debugSession, generation, "not-base64"),
                "debugger_request_invalid",
                TestContext.CancellationToken).ConfigureAwait(false);
            Dictionary<string, object?> invalidActiveStatement = CreateHotReloadArguments(
                debugSession,
                generation,
                "AA==");
            invalidActiveStatement["activeStatements"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["methodToken"] = 0x06000001,
                    ["methodVersion"] = 1,
                    ["oldIlOffset"] = 0,
                    ["startLine"] = 2,
                    ["startColumn"] = 4,
                    ["endLine"] = 1,
                    ["endColumn"] = 4
                }
            };
            await AssertToolErrorAsync(
                mcp.Client,
                "debug_hot_reload",
                invalidActiveStatement,
                "debugger_request_invalid",
                TestContext.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, object?> CreateHotReloadArguments(
        string debugSession,
        long stopGeneration,
        string delta) =>
        new()
        {
            ["debugSession"] = debugSession,
            ["stopGeneration"] = stopGeneration,
            ["moduleId"] = 1,
            ["expectedModuleGeneration"] = 0,
            ["metadataDeltaBase64"] = delta,
            ["ilDeltaBase64"] = delta,
            ["pdbDeltaBase64"] = delta,
            ["activeStatements"] = Array.Empty<object>()
        };
}

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises qualified static storage through each source language and the real DAP adapter.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reads constants and static storage without resuming the selected source frame.
    /// </summary>
    /// <param name="language">The checked-in compiler fixture language.</param>
    [TestMethod]
    [DataRow("CSharp")]
    [DataRow("VisualBasic")]
    [DataRow("FSharp")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task QualifiedStaticFieldsPreserveStoppedFrame(string language)
    {
        string project = $"Csls.Debugger.Fixtures.{language}";
        (string extension, string marker) = language switch
        {
            "CSharp" => ("cs", "answer++;"),
            "VisualBasic" => ("vb", "answer += 1"),
            _ => ("fs", "answer <- answer + 1")
        };
        string source = Path.Join(FindRepositoryRoot(), "test-assets", project, $"Program.{extension}");
        int line = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false), marker);
        string signal = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await InitializeAndLaunchAsync(client, DebuggerLanguageFixtures.GetProgramPath(project, "Debug"), signal,
                suppressJitOptimizations: true).ConfigureAwait(false);
            int threadId = await ConfigureBreakpointAsync(client, source, line).ConfigureAwait(false);
            int frameId = await AssertStoppedFrameAsync(client, threadId, source, line).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId,
                $"{project}.DebuggerFixtureValue.s_number", "61", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId,
                "DebuggerFixtureValue.s_number", "61", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId,
                "System.Int32.MaxValue", "2147483647", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId,
                "System.String.Empty", "\"\"", "string").ConfigureAwait(false);
            if (language == "VisualBasic")
            {
                await AssertStructAssignmentEvaluationAsync(client, frameId,
                    "CSLS.DEBUGGER.FIXTURES.VISUALBASIC.debuggerfixturevalue.S_NUMBER", "61", "int").ConfigureAwait(false);
            }
            JsonElement[] completions = await ReadCompletionsAsync(client, frameId,
                $"{project}.DebuggerFixtureValue.s_", TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement completion = Assert.ContainsSingle(completions);
            Assert.AreEqual("s_number", completion.GetProperty("label").GetString());
            Assert.AreEqual("field", completion.GetProperty("type").GetString());
            await AssertStaticAssignmentAsync(client, frameId, $"{project}.DebuggerFixtureValue.s_number").ConfigureAwait(false);
            Assert.AreEqual(frameId, await AssertStoppedFrameAsync(client, threadId, source, line).ConfigureAwait(false));
            await AssertStructAssignmentEvaluationAsync(client, frameId,
                $"{project}.DebuggerFixtureValue.ReadStaticNumber()", "73", "int").ConfigureAwait(false);
            await AssertAssignmentInvalidationAsync(client, targetCodeExecuted: true, TestContext.CancellationToken).ConfigureAwait(false);
            _ = await AssertStoppedFrameAsync(client, threadId, source, line).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(signal);
        }
    }

    private async Task AssertStaticAssignmentAsync(DapTestClient client, int frameId, string expression)
    {
        JsonElement result = await ReadSetExpressionAsync(client, frameId, expression, "73", success: true,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("73", result.GetProperty("value").GetString());
        Assert.AreEqual("int", result.GetProperty("type").GetString());
        await AssertStructAssignmentEvaluationAsync(client, frameId, expression, "73", "int").ConfigureAwait(false);
        foreach ((string target, string value) in new[] { ("System.String.Empty", "nullReference"), ("System.Int32.MaxValue", "12") })
        {
            JsonElement rejected = await ReadSetExpressionAsync(client, frameId, target, value, success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("readonly or constant", message);
        }
        JsonElement incompatible = await ReadSetExpressionAsync(client, frameId, expression, "nullReference", success: false,
            TestContext.CancellationToken).ConfigureAwait(false);
        string? incompatibleMessage = incompatible.GetProperty("message").GetString();
        Assert.IsNotNull(incompatibleMessage);
        Assert.IsNotEmpty(incompatibleMessage);
        await AssertStructAssignmentEvaluationAsync(client, frameId, expression, "73", "int").ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, frameId, "System.String.Empty", "\"\"", "string").ConfigureAwait(false);
        await AssertStructAssignmentEvaluationAsync(client, frameId, "System.Int32.MaxValue", "2147483647", "int").ConfigureAwait(false);
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies explicit managed break requests through real target execution and DAP inspection.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Honors consecutive break requests during continue and step-over, retaining the calling thread and values.
    /// </summary>
    /// <param name="configuration">The fixture compilation configuration.</param>
    /// <param name="command">The execution request interrupted by the first managed break.</param>
    [TestMethod]
    [DataRow("Debug", "continue")]
    [DataRow("Debug", "next")]
    [DataRow("Release", "continue")]
    [DataRow("Release", "next")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedBreakInterruptsExecutionAndPreservesInspection(string configuration, string command)
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "test-assets", "Csls.Debugger.Fixtures.CSharp",
            "ManagedBreakFixture.cs");
        string[] source = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        int callLine = FindSourceLine(source, "int result = ReadNumber(number);");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initialThread;
        try
        {
            initialThread = await LaunchToSourceBreakpointAsync(client, sourcePath, callLine,
                ["--managed-break"], GetJitFixture(configuration), suppressJitOptimizations: true).ConfigureAwait(false);
        }
        catch
        {
            TestContext.WriteLine(client.ProtocolTranscript);
            throw;
        }

        int firstThread = await StepAndReadStopAsync(client, command, initialThread,
            TestContext.CancellationToken, expectedReason: "pause").ConfigureAwait(false);
        Assert.AreEqual(initialThread, firstThread);
        int firstReference = await AssertManagedBreakArgumentAsync(client, firstThread, sourcePath, "42").ConfigureAwait(false);
        int secondThread = await StepAndReadStopAsync(client, "continue", firstThread,
            TestContext.CancellationToken, expectedReason: "pause").ConfigureAwait(false);
        Assert.AreEqual(initialThread, secondThread);
        _ = await AssertManagedBreakArgumentAsync(client, secondThread, sourcePath, "43").ConfigureAwait(false);

        int staleSequence = await client.SendRequestAsync("variables", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("variablesReference", firstReference);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument stale = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(stale.RootElement, staleSequence, "variables", success: false);
        JsonElement evaluationFrame = await ReadTopSourceFrameAsync(client, secondThread).ConfigureAwait(false);
        JsonElement evaluation = await ReadEvaluationAsync(client, evaluationFrame.GetProperty("id").GetInt32(),
            "Csls.Debugger.Fixtures.CSharp.ManagedBreakFixture.ReadNumber(64)",
            success: true, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("65", evaluation.GetProperty("result").GetString());
        Assert.AreEqual("int", evaluation.GetProperty("type").GetString());
        _ = await AssertManagedBreakArgumentAsync(client, secondThread, sourcePath, "43").ConfigureAwait(false);
        int continueSequence = await client.SendRequestAsync("continue", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, continueSequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    private async Task<int> AssertManagedBreakArgumentAsync(DapTestClient client, int threadId,
        string sourcePath, string expected)
    {
        JsonElement frame = await ReadTopSourceFrameAsync(client, threadId).ConfigureAwait(false);
        Assert.AreEqual("Csls.Debugger.Fixtures.CSharp.ManagedBreakFixture.ReadNumber", frame.GetProperty("name").GetString());
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, frame.GetProperty("source").GetProperty("path").GetString()));
        (int arguments, _) = await ReadFrameScopeReferencesAsync(client, frame.GetProperty("id").GetInt32()).ConfigureAwait(false);
        JsonElement argument = Assert.ContainsSingle(await ReadVariablesAsync(client, arguments).ConfigureAwait(false));
        Assert.AreEqual("number", argument.GetProperty("name").GetString());
        Assert.AreEqual("int", argument.GetProperty("type").GetString());
        Assert.AreEqual(expected, argument.GetProperty("value").GetString());
        return arguments;
    }
}

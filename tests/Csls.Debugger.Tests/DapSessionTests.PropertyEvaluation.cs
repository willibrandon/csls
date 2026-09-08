using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies supervised property evaluation and automatic execution policy against a real managed target.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Selects exact hidden property declarations while preserving virtual dispatch through a base cast.
    /// </summary>
    /// <param name="expression">The property expression containing the selected receiver type.</param>
    /// <param name="expected">The value expected from the selected declaration and runtime receiver.</param>
    [TestMethod]
    [DataRow("((Csls.TestProcessHost.ReferenceCastBase)hiddenObject).ValueProperty", "11")]
    [DataRow("((Csls.TestProcessHost.ReferenceCastDerived)hiddenBase).ValueProperty", "22")]
    [DataRow("((Csls.TestProcessHost.ReferenceCastBase)hiddenObject).VirtualValueProperty", "222")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PropertyEvaluationSelectsDeclarationsAndVirtualDispatch(string expression, string expected)
    {
        string signal = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(signal, "ReferenceAssignmentFixture.cs",
                "int result = DebuggerFixture.WaitForSignal(", "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement result = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, result.GetProperty("result").GetString());
            Assert.AreEqual("int", result.GetProperty("type").GetString());
            await AssertAssignmentInvalidationAsync(client, targetCodeExecuted: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(signal);
        }
    }

    /// <summary>
    /// Executes an authorized mutating getter once and preserves stopped storage when automatic execution is disabled.
    /// </summary>
    /// <param name="allowImplicitFuncEval">Whether the session permits automatic property evaluation.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PropertyEvaluationHonorsAutomaticExecutionPolicy(bool allowImplicitFuncEval)
    {
        string signal = Path.Join(Path.GetTempPath(), $"csls-getter-execution-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(signal, allowImplicitFuncEval: allowImplicitFuncEval).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture diagnostics = CaptureProtocolOnCancellation(client);
            await ReachImplicitEvaluationBreakpointAsync(client).ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement result = await ReadEvaluationAsync(client, frameId, "localObject.MutatingNumberProperty",
                success: allowImplicitFuncEval, TestContext.CancellationToken).ConfigureAwait(false);
            if (allowImplicitFuncEval)
            {
                Assert.AreEqual("43", result.GetProperty("result").GetString());
                Assert.AreEqual("int", result.GetProperty("type").GetString());
                using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            JsonElement retained = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, retained.GetProperty("id").GetInt32());
            JsonElement field = await ReadEvaluationAsync(client, frameId, "localObject.Number", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(allowImplicitFuncEval ? "43" : "42", field.GetProperty("result").GetString());
            JsonElement finallyResult = await ReadEvaluationAsync(client, frameId, "localObject.FinallyNumberProperty",
                success: allowImplicitFuncEval, TestContext.CancellationToken).ConfigureAwait(false);
            if (allowImplicitFuncEval)
            {
                Assert.AreEqual("43", finallyResult.GetProperty("result").GetString());
                using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            field = await ReadEvaluationAsync(client, frameId, "localObject.Number", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(allowImplicitFuncEval ? "44" : "42", field.GetProperty("result").GetString());
            await ResumeAndReleaseFixtureAsync(client, signal).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(signal);
        }
    }

    /// <summary>
    /// Retains getter-result identity and tuple names and recovers the same frame after a throwing getter.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task PropertyEvaluationPreservesResultsAndRecoversFromException()
    {
        string signal = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(signal).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            using DapTestCancellationCapture diagnostics = CaptureProtocolOnCancellation(client);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement nested = await ReadEvaluationAsync(client, frameId,
                "localObject.MutatingSelfProperty.MutatingSelfProperty", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("requires target-code evaluation", nested.GetProperty("message").GetString()!);
            JsonElement before = await ReadEvaluationAsync(client, frameId, "localObject.Number", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", before.GetProperty("result").GetString());
            JsonElement self = await ReadEvaluationAsync(client, frameId, "localObject.MutatingSelfProperty", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            int reference = self.GetProperty("variablesReference").GetInt32();
            JsonElement[] fields = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
            Assert.AreEqual("43", fields.Single(item => item.GetProperty("name").GetString() == "Number").GetProperty("value").GetString());
            _ = await ReadSetVariableAsync(client, reference, "Number", "99", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement original = await ReadEvaluationAsync(client, frameId, "localObject.Number", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("99", original.GetProperty("result").GetString());

            JsonElement pair = await ReadEvaluationAsync(client, frameId, "localObject.ComputedPairProperty", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("(100, \"answer!\")", pair.GetProperty("result").GetString());
            Assert.AreEqual("(int Next, string Caption)", pair.GetProperty("type").GetString());
            using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            JsonElement[] elements = await ReadVariablesAsync(client, pair.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            Assert.AreEqual("100", elements.Single(item => item.GetProperty("name").GetString() == "Next").GetProperty("value").GetString());
            Assert.AreEqual("\"answer!\"", elements.Single(item => item.GetProperty("name").GetString() == "Caption").GetProperty("value").GetString());
            JsonElement stale = await ReadSetVariableAsync(client, reference, "Number", "77", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("stale or unknown", stale.GetProperty("message").GetString()!);
            _ = await ReadSetVariableAsync(client, pair.GetProperty("variablesReference").GetInt32(), "Next", "88",
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement failure = await ReadEvaluationAsync(client, frameId, "localObject.ThrowingNumberProperty", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("threw System.InvalidOperationException", failure.GetProperty("message").GetString()!);
            using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(invalidated.RootElement, "invalidated");
            }
            JsonElement retained = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            AssertSameLogicalFrame(frame, retained);
            original = await ReadEvaluationAsync(client, frameId, "localObject.Number", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("99", original.GetProperty("result").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(signal);
            File.Delete(signal + ".evaluation");
        }
    }
}

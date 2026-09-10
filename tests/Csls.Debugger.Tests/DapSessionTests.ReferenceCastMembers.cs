using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies method selection and completion against explicitly cast receiver declarations.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Matches compiler-selected hidden methods while preserving virtual dispatch through a base cast.
    /// </summary>
    /// <param name="expression">The cast receiver's method invocation.</param>
    /// <param name="oracle">The local containing the same invocation compiled into the target.</param>
    /// <param name="expected">The independently expected result from that declaration.</param>
    [TestMethod]
    [DataRow("((Csls.TestProcessHost.ReferenceCastBase)hiddenObject).GetValue()", "hiddenBaseMethodOracle", "11")]
    [DataRow("((Csls.TestProcessHost.ReferenceCastDerived)hiddenBase).GetValue()", "hiddenDerivedMethodOracle", "22")]
    [DataRow("((Csls.TestProcessHost.ReferenceCastBase)hiddenObject).GetVirtualValue()", "hiddenVirtualMethodOracle", "222")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceCastMembersMatchCompilerMethodSelection(string expression, string oracle, string expected)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, oracle, expected, "int").ConfigureAwait(false);
            JsonElement evaluated = await ReadEvaluationAsync(client, frameId, expression, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expected, evaluated.GetProperty("result").GetString());
            Assert.AreEqual("int", evaluated.GetProperty("type").GetString());
            await AssertAssignmentInvalidationAsync(client, targetCodeExecuted: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Offers only members visible through the explicit receiver type and rejects hidden derived-only calls.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceCastMembersExcludeDerivedDeclarations()
    {
        const string BaseReceiver = "((Csls.TestProcessHost.ReferenceCastBase)hiddenObject)";
        const string DerivedReceiver = "((Csls.TestProcessHost.ReferenceCastDerived)hiddenBase)";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement[] baseMembers = await ReadCompletionsAsync(client, frameId, BaseReceiver + ".Get",
                TestContext.CancellationToken).ConfigureAwait(false);
            string[] labels = [.. baseMembers.Select(static item => item.GetProperty("label").GetString()!)];
            Assert.Contains("GetValue", labels);
            Assert.Contains("GetVirtualValue", labels);
            Assert.DoesNotContain("GetDerivedValue", labels);
            JsonElement[] derivedMembers = await ReadCompletionsAsync(client, frameId, DerivedReceiver + ".GetDerived",
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement derived = Assert.ContainsSingle(derivedMembers);
            Assert.AreEqual("GetDerivedValue", derived.GetProperty("label").GetString());
            Assert.AreEqual("method", derived.GetProperty("type").GetString());
            JsonElement denied = await ReadEvaluationAsync(client, frameId, BaseReceiver + ".GetDerivedValue()",
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("No instance method named 'GetDerivedValue'", denied.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "hiddenBaseOracle", "11", "int").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

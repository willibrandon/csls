using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies reference casts and type tests against actual stopped runtime values over DAP.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rejects ambiguous type names instead of choosing a same-named definition from another load context.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceTypeExpressionsRejectAmbiguousLoadContexts()
    {
        const string Source = "localResultsViewIsolatedContext._items[0]";
        const string Destination = "localResultsViewDefaultContext._items[0]";
        const string TypeName = "Csls.TestProcessHost.ResultsViewElement";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath, isolateResultsViewAssembly: true).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            foreach (string expression in new[] { $"({TypeName}){Source}", $"{Source} as {TypeName}", $"{Source} is {TypeName}" })
            {
                JsonElement rejected = await ReadEvaluationAsync(client, frameId, expression, success: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
                string message = rejected.GetProperty("message").GetString() ?? string.Empty;
                Assert.Contains("ambiguous across loaded modules", message, StringComparison.Ordinal);
                Assert.Contains(TypeName, message, StringComparison.Ordinal);
            }

            await AssertReferenceElementFieldAsync(client, frameId, Source).ConfigureAwait(false);
            await AssertReferenceElementFieldAsync(client, frameId, Destination).ConfigureAwait(false);
            JsonElement refreshed = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, refreshed.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Evaluates type operations without changing the target's stop, object contents, or evaluation count.
    /// </summary>
    /// <param name="expression">The source-language type operation.</param>
    /// <param name="expectedValue">The exact debugger value.</param>
    /// <param name="expectedType">The resulting debugger type.</param>
    [TestMethod]
    [DataRow("((System.ArgumentException)widenedSource)._message", "\"widened source\"", "string")]
    [DataRow("(widenedSource as System.ArgumentException)._message", "\"widened source\"", "string")]
    [DataRow("widenedSource is System.ArgumentException", "true", "bool")]
    [DataRow("widenedSource is System.InvalidOperationException", "false", "bool")]
    [DataRow("nullBaseTarget is System.Exception", "false", "bool")]
    [DataRow("widenedSource as System.InvalidOperationException", "null", "System.InvalidOperationException")]
    [DataRow("(System.ArgumentException)nullBaseTarget", "null", "System.ArgumentException")]
    [DataRow("enumerableSource is System.Collections.Generic.IEnumerable<System.Exception>", "true", "bool")]
    [DataRow("derivedArray is System.Exception[]", "true", "bool")]
    [DataRow("boxedSource is int", "true", "bool")]
    [DataRow("boxedSource is long", "false", "bool")]
    [DataRow("null is object", "false", "bool")]
    [DataRow("(int)boxedSource", "42", "int")]
    [DataRow("((object)\"text\") is string", "true", "bool")]
    [DataRow("(interfaceTarget as string)", "null", "string")]
    [DataRow("(derivedArray as System.Collections.Generic.IEnumerable<System.Exception>) is System.ArgumentException[]", "true", "bool")]
    [DataRow("(covariantFactory as System.Func<System.ArgumentException>) is System.Func<System.ArgumentException>", "true", "bool")]
    [DataRow("(contravariantAction as System.Action<string>) is System.Action<object>", "true", "bool")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceTypeExpressionsPreserveStoppedState(
        string expression, string expectedValue, string expectedType)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement result = await ReadEvaluationAsync(
                client, frameId, expression, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expectedValue, result.GetProperty("result").GetString());
            Assert.AreEqual(expectedType, result.GetProperty("type").GetString());
            JsonElement calls = await ReadEvaluationAsync(
                client, frameId, "factory._calls", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("0", calls.GetProperty("result").GetString());
            await AssertStringIdentityExpressionAsync(
                client, frameId, "widenedSource._message", "\"widened source\"").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "covariantCastOracle", "true", "bool").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "contravariantCastOracle", "true", "bool").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects incompatible declared casts and invalid runtime downcasts without changing target state.
    /// </summary>
    /// <param name="expression">The rejected reference or unboxing expression.</param>
    /// <param name="diagnostic">The required type-compatibility diagnostic.</param>
    [TestMethod]
    [DataRow("(System.InvalidOperationException)widenedSource", "cannot be cast")]
    [DataRow("(string)widenedSource", "No built-in reference conversion")]
    [DataRow("(System.Collections.Generic.List<System.Exception>)enumerableSource", "No built-in reference conversion")]
    [DataRow("derivedArray as int[]", "No built-in reference conversion")]
    [DataRow("(long)boxedSource", "long")]
    [DataRow("(INT)boxedSource", "INT")]
    [DataRow("(system.Int32)boxedSource", "system.Int32")]
    [DataRow("(CInt)boxedSource", "CInt")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceTypeExpressionsRejectIncompatibleCasts(string expression, string diagnostic)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement failure = await ReadEvaluationAsync(
                client, frameId, expression, success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(diagnostic, failure.GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "factory._calls", "0", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "boxedSource", "42", "int").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Selects hidden fields by explicit declaration and writes only that declaration's physical storage.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceTypeExpressionsSelectHiddenFieldStorage()
    {
        const string BaseField = "((Csls.TestProcessHost.ReferenceCastBase)hiddenObject)._value";
        const string DerivedField = "((Csls.TestProcessHost.ReferenceCastDerived)hiddenBase)._value";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "hiddenBaseOracle", "11", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "hiddenDerivedOracle", "22", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, BaseField, "11", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, DerivedField, "22", "int").ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(
                client, frameId, BaseField, "33", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("33", assigned.GetProperty("value").GetString());
            await AssertStructAssignmentEvaluationAsync(client, frameId, BaseField, "33", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, DerivedField, "22", "int").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

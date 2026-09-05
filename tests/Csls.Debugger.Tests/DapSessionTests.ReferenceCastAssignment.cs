using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies cast declarations survive assignment and guarded runtime materialization.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Requires an exact explicit unboxing conversion before assigning an existing boxed primitive.
    /// </summary>
    /// <param name="source">The boxed source expression retaining its reference declaration.</param>
    [TestMethod]
    [DataRow("boxedSource")]
    [DataRow("(object)boxedSource")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceCastAssignmentRequiresExplicitUnboxing(string source)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement rejected = await ReadSetExpressionAsync(client, frameId, "hiddenDerived._value", source,
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("explicit unboxing conversion", rejected.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "hiddenDerived._value", "22", "int").ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(client, frameId, "hiddenDerived._value", $"(int){source}",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", assigned.GetProperty("value").GetString());
            Assert.AreEqual("int", assigned.GetProperty("type").GetString());
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
    /// Checks the actual covariant array storage even when a cast widens its declared element type.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceCastAssignmentPreservesCovariantArrayStorage()
    {
        const string Destination = "((System.Exception[])derivedArray)[0]";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement rejected = await ReadSetExpressionAsync(client, frameId, Destination, "baseTarget",
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("Array storage", rejected.GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(client, frameId, "derivedArray[0]._message", "\"replacement element\"")
                .ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(client, frameId, Destination, "(System.ArgumentException)widenedSource",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("System.ArgumentException", assigned.GetProperty("type").GetString());
            await AssertStringIdentityExpressionAsync(client, frameId, "derivedArray[0]._message", "\"widened source\"")
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
    /// Rejects assignment from a wider cast declaration before changing the destination or running target code.
    /// </summary>
    /// <param name="target">The writable destination.</param>
    /// <param name="source">The incompatible cast expression.</param>
    /// <param name="inspection">The unchanged destination value to inspect.</param>
    /// <param name="expected">The destination's original contents.</param>
    [TestMethod]
    [DataRow("derivedTarget", "(System.Exception)derivedSource", "derivedTarget._message", "\"original derived\"")]
    [DataRow("target", "(System.Exception)null", "target", "\"reference-assignment-value\"")]
    [DataRow("target", "(object)\"new value\"", "target", "\"reference-assignment-value\"")]
    [DataRow("target", "(System.Collections.Generic.IEnumerable<char>)\"new value\"", "target", "\"reference-assignment-value\"")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceCastAssignmentRejectsImplicitDowncast(
        string target, string source, string inspection, string expected)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, target, source, success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("No implicit reference conversion", rejected.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(client, frameId, inspection, expected).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "factory._calls", "0", "int").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Assigns explicit downcasts and typed nulls without executing target code or changing frame identity.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceCastAssignmentPreservesReferenceAndNullTypes()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(client, frameId, "derivedTarget",
                "(System.ArgumentException)widenedSource", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("System.ArgumentException", assigned.GetProperty("type").GetString());
            await AssertStringIdentityExpressionAsync(client, frameId, "derivedTarget._message", "\"widened source\"")
                .ConfigureAwait(false);
            JsonElement cleared = await ReadSetExpressionAsync(client, frameId, "derivedTarget",
                "(System.ArgumentException)nullBaseTarget", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("null", cleared.GetProperty("value").GetString());
            Assert.AreEqual("System.ArgumentException", cleared.GetProperty("type").GetString());
            await AssertStringIdentityExpressionAsync(client, frameId, "widenedSource._message", "\"widened source\"")
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
    /// Materializes compatible string casts and revalidates the destination in the replacement generation.
    /// </summary>
    /// <param name="target">The compatible reference destination.</param>
    /// <param name="source">The string cast to materialize.</param>
    [TestMethod]
    [DataRow("objectTarget", "(object)\"allocated text\"")]
    [DataRow("interfaceTarget", "(System.Collections.Generic.IEnumerable<char>)\"allocated text\"")]
    [DataRow("target", "(string)(object)\"allocated text\"")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceCastAssignmentMaterializesCompatibleStrings(string target, string source)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(client, frameId, target, source, success: true,
                TestContext.CancellationToken, targetCodeExecuted: true).ConfigureAwait(false);
            Assert.AreEqual("\"allocated text\"", assigned.GetProperty("value").GetString());
            Assert.AreEqual("string", assigned.GetProperty("type").GetString());
            await AssertStringIdentityExpressionAsync(client, frameId, target, "\"allocated text\"").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

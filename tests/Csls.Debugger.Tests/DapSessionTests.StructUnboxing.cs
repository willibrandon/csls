using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies explicit struct unboxing and value-copy semantics through a stopped real process.
/// </summary>
public sealed partial class DapSessionTests
{
    private const string UnboxedStructType = "System.ValueTuple<int, System.Runtime.CompilerServices.StrongBox<int>>";
    private const string UnboxedStructExpression = "(" + UnboxedStructType + ")boxedStruct";

    /// <summary>
    /// Inspects exact unboxed values without running target code or changing the boxed fields.
    /// </summary>
    /// <param name="source">The boxed or already unboxed source expression.</param>
    /// <param name="expectedNumber">The value of the copied integer field.</param>
    /// <param name="expectedReference">The value stored in the referenced object.</param>
    [TestMethod]
    [DataRow("boxedStruct", "17", "23")]
    [DataRow("(System.ValueType)boxedStruct", "17", "23")]
    [DataRow("(System.IComparable)boxedStruct", "17", "23")]
    [DataRow("structTarget", "31", "37")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructUnboxingPreservesStoppedState(string source, string expectedNumber, string expectedReference)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture protocolCapture = CaptureProtocolOnCancellation(client);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            string expression = $"({UnboxedStructType}){source}";
            JsonElement value = await ReadEvaluationAsync(client, frameId, expression,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsGreaterThan(0, value.GetProperty("variablesReference").GetInt32());
            JsonElement[] children = await ReadVariablesAsync(client, value.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            JsonElement number = Assert.ContainsSingle(children.Where(child => child.GetProperty("name").GetString() == "Item1"));
            Assert.AreEqual(expectedNumber, number.GetProperty("value").GetString());
            string? childExpression = number.GetProperty("evaluateName").GetString();
            Assert.IsNotNull(childExpression);
            Assert.Contains("System.ValueTuple", childExpression, StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, childExpression, expectedNumber, "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, $"({expression}).Item1", expectedNumber, "int")
                .ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, $"({expression}).Item2.Value", expectedReference, "int")
                .ConfigureAwait(false);
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
    /// Copies struct fields into real storage while preserving embedded reference identity and the original box.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructUnboxingAssignmentCopiesValueAndSharesEmbeddedReference()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture protocolCapture = CaptureProtocolOnCancellation(client);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement rejected = await ReadSetExpressionAsync(client, frameId, "structTarget", "boxedStruct",
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("unboxed value types", rejected.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "structTarget.Number", "31", "int").ConfigureAwait(false);
            _ = await ReadSetExpressionAsync(client, frameId, "structTarget", UnboxedStructExpression,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "structTarget.Number", "17", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "structTarget.Reference.Value", "23", "int").ConfigureAwait(false);
            _ = await ReadSetExpressionAsync(client, frameId, "structTarget.Number", "41",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, $"({UnboxedStructExpression}).Item1", "17", "int")
                .ConfigureAwait(false);
            _ = await ReadSetExpressionAsync(client, frameId, $"({UnboxedStructExpression}).Item2.Value", "43",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "structTarget.Reference.Value", "43", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "structTarget.Number", "41", "int").ConfigureAwait(false);
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
    /// Rejects writes through an unboxing result using both expression and retained-variable entry points.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructUnboxingRejectsWritesThroughTemporary()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture protocolCapture = CaptureProtocolOnCancellation(client);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement value = await ReadEvaluationAsync(client, frameId, UnboxedStructExpression,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            int reference = value.GetProperty("variablesReference").GetInt32();
            JsonElement rejectedExpression = await ReadSetExpressionAsync(client, frameId,
                $"({UnboxedStructExpression}).Item1", "47", success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("physical storage", rejectedExpression.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            JsonElement rejectedVariable = await ReadSetVariableAsync(client, reference, "Item1", "53",
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("physical storage", rejectedVariable.GetProperty("message").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, $"({UnboxedStructExpression}).Item1", "17", "int")
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
    /// Retains copied fields when their original local or boxed reference is subsequently assigned.
    /// </summary>
    /// <param name="source">The boxed or unboxed struct being copied.</param>
    /// <param name="destination">The original storage changed after the copy.</param>
    /// <param name="replacement">The replacement assigned to the original storage.</param>
    /// <param name="expectedNumber">The integer retained by the copy.</param>
    /// <param name="expectedReplacement">The value observed in the changed storage.</param>
    [TestMethod]
    [DataRow("boxedStruct", "boxedStruct", "boxedSource", "17", "42")]
    [DataRow("structTarget", "structTarget.Number", "59", "31", "59")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructUnboxingRetainsValueSnapshot(
        string source, string destination, string replacement, string expectedNumber, string expectedReplacement)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture protocolCapture = CaptureProtocolOnCancellation(client);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement snapshot = await ReadEvaluationAsync(client, frameId, $"({UnboxedStructType}){source}",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            int reference = snapshot.GetProperty("variablesReference").GetInt32();
            _ = await ReadSetExpressionAsync(client, frameId, destination, replacement,
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, destination, expectedReplacement, "int").ConfigureAwait(false);
            JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
            JsonElement number = Assert.ContainsSingle(children.Where(child => child.GetProperty("name").GetString() == "Item1"));
            Assert.AreEqual(expectedNumber, number.GetProperty("value").GetString());
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects null and different closed value types before assigning any destination storage.
    /// </summary>
    /// <param name="source">The incompatible source expression.</param>
    [TestMethod]
    [DataRow("(" + UnboxedStructType + ")nullBox")]
    [DataRow("(System.ValueTuple<long, System.Runtime.CompilerServices.StrongBox<int>>)boxedStruct")]
    [DataRow("(" + UnboxedStructType + ")boxedSource")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructUnboxingRejectsDifferentRuntimeTypeAndNull(string source)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture protocolCapture = CaptureProtocolOnCancellation(client);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement rejected = await ReadSetExpressionAsync(client, frameId, "structTarget", source,
                success: false, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains("cannot convert", rejected.GetProperty("message").GetString() ?? string.Empty, StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "structTarget.Number", "31", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "structTarget.Reference.Value", "37", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "factory._calls", "0", "int").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

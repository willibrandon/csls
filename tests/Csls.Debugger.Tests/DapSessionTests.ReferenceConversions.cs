using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies declared reference conversion semantics and post-write runtime inspection over DAP.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Uses a guarded call's declared return type when validating its assignment result.
    /// </summary>
    [TestMethod]
    [DataRow("baseTarget", "factory.GetValue()", true, "evaluated replacement")]
    [DataRow("derivedTarget", "factory.GetValue()", false, "original derived")]
    [DataRow("target", "factory.GetNull()", false, "reference-assignment-value")]
    [DataRow("baseTarget", "factory.GetGenericValue()", true, "generic evaluated replacement")]
    [DataRow("derivedTarget", "factory.GetGenericValue()", false, "original derived")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsPreserveDeclaredCallResults(string target, string source, bool succeeds, string expected)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement response = await ReadSetExpressionAsync(
                client, frameId, target, source, succeeds, TestContext.CancellationToken,
                targetCodeExecuted: true).ConfigureAwait(false);
            if (succeeds)
            {
                Assert.AreEqual("System.ArgumentException", response.GetProperty("type").GetString());
            }
            else
            {
                Assert.Contains("System.Exception", response.GetProperty("message").GetString() ?? string.Empty,
                    StringComparison.Ordinal);
            }

            frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            string expression = target == "target" ? target : $"{target}._message";
            await AssertStringIdentityExpressionAsync(client, frameId, expression, $"\"{expected}\"").ConfigureAwait(false);
            JsonElement calls = await ReadEvaluationAsync(
                client, frameId, "factory._calls", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("1", calls.GetProperty("result").GetString());
            Assert.AreEqual("int", calls.GetProperty("type").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Preserves existing boxed references without performing an implicit boxing allocation.
    /// </summary>
    [TestMethod]
    [DataRow("objectTarget")]
    [DataRow("objectArray[0]")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsPreserveExistingBoxes(string target)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(
                client, frameId, target, "boxedSource", success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("int", assigned.GetProperty("type").GetString());
            Assert.AreEqual("42", assigned.GetProperty("value").GetString());
            _ = await ReadSetExpressionAsync(
                client, frameId, target, "43", success: false, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement preserved = await ReadEvaluationAsync(
                client, frameId, target, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("int", preserved.GetProperty("type").GetString());
            Assert.AreEqual("42", preserved.GetProperty("result").GetString());
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Keeps type and method generic substitutions distinct for locals, arguments, and fields.
    /// </summary>
    [TestMethod]
    [DataRow("genericTarget", "genericSource")]
    [DataRow("genericBase", "genericDerived")]
    [DataRow("genericHolder.Value", "genericDerived")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsBindClosedGenericDeclarations(string target, string source)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(
                client, frameId, target, source, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("System.ArgumentException", assigned.GetProperty("type").GetString());
            await AssertStringIdentityExpressionAsync(
                client, frameId, $"{target}._message", "\"generic replacement\"").ConfigureAwait(false);
            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, source, target, success: false, TestContext.CancellationToken).ConfigureAwait(false);
            string message = rejected.GetProperty("message").GetString() ?? string.Empty;
            Assert.Contains("System.Exception", message, StringComparison.Ordinal);
            Assert.Contains("System.ArgumentException", message, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(
                client, frameId, $"{source}._message", "\"generic replacement\"").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Assigns references through implemented and covariant generic interfaces.
    /// </summary>
    [TestMethod]
    [DataRow("interfaceTarget", "textSource", "string")]
    [DataRow("enumerableTarget", "enumerableSource", "System.Collections.Generic.List<System.ArgumentException>")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsSupportInterfaces(string target, string source, string runtimeType)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement result = await ReadSetExpressionAsync(
                client, frameId, target, source, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(runtimeType, result.GetProperty("type").GetString());
            JsonElement assigned = await ReadEvaluationAsync(
                client, frameId, target, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement original = await ReadEvaluationAsync(
                client, frameId, source, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(runtimeType, assigned.GetProperty("type").GetString());
            Assert.AreEqual(original.GetProperty("result").GetString(), assigned.GetProperty("result").GetString());
            string member = target == "interfaceTarget" ? target : $"{target}._items[0]._message";
            string expected = target == "interfaceTarget" ? "\"interface replacement\"" : "\"covariant element\"";
            await AssertStringIdentityExpressionAsync(client, frameId, member, expected).ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects invariant generic conversions without changing either collection.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsRejectInvariantGenericArguments()
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
                client, frameId, "invariantTarget", "enumerableSource", success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string message = rejected.GetProperty("message").GetString() ?? string.Empty;
            Assert.Contains("System.Exception", message, StringComparison.Ordinal);
            Assert.Contains("System.ArgumentException", message, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "invariantTarget._items[0]._message", "\"original invariant\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "enumerableSource._items[0]._message", "\"covariant element\"").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Allows covariant arrays while enforcing the actual array element type before every write.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsEnforceCovariantArrayStores()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(
                client, frameId, "arrayTarget", "derivedArray", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("System.ArgumentException[]", assigned.GetProperty("type").GetString());
            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, "arrayTarget[0]", "baseTarget", success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string message = rejected.GetProperty("message").GetString() ?? string.Empty;
            Assert.Contains("Array storage", message, StringComparison.Ordinal);
            Assert.Contains("System.ArgumentException", message, StringComparison.Ordinal);
            Assert.Contains("System.InvalidOperationException", message, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "arrayTarget[0]._message", "\"replacement element\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "baseTarget._message", "\"original base\"").ConfigureAwait(false);
            JsonElement written = await ReadSetExpressionAsync(
                client, frameId, "arrayTarget[0]", "derivedSource", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("System.ArgumentException", written.GetProperty("type").GetString());
            await AssertStringIdentityExpressionAsync(
                client, frameId, "derivedArray[0]._message", "\"replacement\"").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Assigns a derived reference through a base or object declaration and refreshes its actual runtime type.
    /// </summary>
    /// <param name="target">The destination declaration to assign.</param>
    [TestMethod]
    [DataRow("baseTarget")]
    [DataRow("nullBaseTarget")]
    [DataRow("objectTarget")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsUseDeclaredDestinationTypes(string target)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement result = await ReadSetExpressionAsync(
                client, frameId, target, "derivedSource", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("System.ArgumentException", result.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, result.GetProperty("variablesReference").GetInt32());
            JsonElement[] children = await ReadVariablesAsync(client, result.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            JsonElement message = Assert.ContainsSingle(children.Where(
                static child => child.GetProperty("name").GetString() == "_message"));
            Assert.AreEqual("\"replacement\"", message.GetProperty("value").GetString());
            Assert.AreEqual("string", message.GetProperty("type").GetString());
            JsonElement refreshed = await ReadEvaluationAsync(
                client, frameId, target, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("System.ArgumentException", refreshed.GetProperty("type").GetString());
            await AssertStringIdentityExpressionAsync(
                client, frameId, $"{target}._message", "\"replacement\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "derivedSource._message", "\"replacement\"").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects an implicit downcast even when the current referents have the same concrete runtime type.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsRejectImplicitDowncastBeforeMutation()
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
                client, frameId, "derivedTarget", "widenedSource", success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("System.Exception", message, StringComparison.Ordinal);
            Assert.Contains("System.ArgumentException", message, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "derivedTarget._message", "\"original derived\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "widenedSource._message", "\"widened source\"").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Preserves a typed null's declaration instead of treating every null-valued expression as a null literal.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceConversionsRejectIncompatibleTypedNull()
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
                client, frameId, "target", "nullBaseTarget", success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("System.Exception", message, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "target", "\"reference-assignment-value\"").ConfigureAwait(false);
            JsonElement unchangedNull = await ReadEvaluationAsync(
                client, frameId, "nullBaseTarget", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("null", unchangedNull.GetProperty("result").GetString());
            Assert.AreEqual("System.Exception", unchangedNull.GetProperty("type").GetString());
            Assert.AreEqual(0, unchangedNull.GetProperty("variablesReference").GetInt32());
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

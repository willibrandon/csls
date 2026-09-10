using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies context-dependent defaults through real stopped managed storage and DAP assignment.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Clears a string location without materializing the keyword or changing the visible stop.
    /// </summary>
    /// <param name="setVariable">Whether to assign through the named locals container.</param>
    /// <param name="value">The contextual default expression.</param>
    [TestMethod]
    [DataRow(false, "default")]
    [DataRow(true, "default")]
    [DataRow(false, "(default)")]
    [DataRow(true, "(default)")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DefaultAssignmentClearsReferenceWithoutTargetExecution(bool setVariable, string value)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "target", "\"reference-assignment-value\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "alias", "\"reference-assignment-value\"").ConfigureAwait(false);

            JsonElement assigned = setVariable
                ? await ReadSetVariableAsync(client, localsReference, "target", value, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false)
                : await ReadSetExpressionAsync(client, frameId, "target", value, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("null", assigned.GetProperty("value").GetString());
            Assert.AreEqual("string", assigned.GetProperty("type").GetString());
            Assert.AreEqual(0, assigned.GetProperty("variablesReference").GetInt32());
            await AssertStringIdentityExpressionAsync(client, frameId, "target", "null").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(client, frameId, "alias", "null").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Clears primitive and structured locals through both assignment commands while retaining their exact types.
    /// </summary>
    /// <param name="setVariable">Whether to write through the existing locals container.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DefaultAssignmentClearsPrimitiveAndStructuredLocals(bool setVariable)
    {
        (string Name, string Before, string After)[] values =
        [
            ("localNumber", "43", "0"),
            ("localLong", "44", "0"),
            ("localByte", "1", "0"),
            ("localMode", "Second", "0"),
            ("localOptions", "Read | Execute", "None"),
            ("localEscapedCharacter", "'\\n'", "'\\0'"),
            ("localDecimal", "-1234.50", "0"),
            ("localGuid", "00112233-4455-6677-8899-aabbccddeeff", "00000000-0000-0000-0000-000000000000"),
            ("localTuple", "(42, \"answer\")", "(0, null)"),
            ("localNullable", "45", "null"),
            ("localEmptyNullable", "null", "null")
        ];
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId).ConfigureAwait(false);
            foreach ((string name, string before, string after) in values)
            {
                JsonElement original = await ReadEvaluationAsync(client, frameId, name, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(before, original.GetProperty("result").GetString(), name);
                JsonElement assigned = setVariable
                    ? await ReadSetVariableAsync(client, localsReference, name, "default", success: true,
                        TestContext.CancellationToken).ConfigureAwait(false)
                    : await ReadSetExpressionAsync(client, frameId, name, "default", success: true,
                        TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(after, assigned.GetProperty("value").GetString(), name);
                Assert.AreEqual(original.GetProperty("type").GetString(), assigned.GetProperty("type").GetString(), name);
                JsonElement refreshed = await ReadEvaluationAsync(client, frameId, name, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(after, refreshed.GetProperty("result").GetString(), name);
                Assert.AreEqual(original.GetProperty("type").GetString(), refreshed.GetProperty("type").GetString(), name);
            }

            await AssertStructAssignmentGenericStorageAsync(
                client, frameId, "localNullable", "int", "0", nullable: true).ConfigureAwait(false);
            await AssertStructAssignmentGenericStorageAsync(
                client, frameId, "localEmptyNullable", "int", "0", nullable: true).ConfigureAwait(false);
            JsonElement tuple = await ReadEvaluationAsync(client, frameId, "localTuple", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            await AssertStructAssignmentTupleChildrenAsync(
                client, tuple, "localTuple", "Number", "Text", "0", "null").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "number", "42", "int")
                .ConfigureAwait(false);
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects a missing type context and a managed interior pointer without changing its referent.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DefaultAssignmentRejectsUntypedEvaluationAndManagedByReference()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            JsonElement quoted = await ReadEvaluationAsync(client, frameId, "\"default\"", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("\"default\"", quoted.GetProperty("result").GetString());
            Assert.AreEqual("string", quoted.GetProperty("type").GetString());
            JsonElement untyped = await ReadEvaluationAsync(client, frameId, "default", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("A default literal requires a destination type.", untyped.GetProperty("message").GetString());
            JsonElement byReference = await ReadSetExpressionAsync(client, frameId, "alias", "default", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = byReference.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("managed by-reference", message, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "target", "\"reference-assignment-value\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "alias", "\"reference-assignment-value\"").ConfigureAwait(false);
            Assert.AreEqual(frameId, await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Clears argument, field, and array tuple storage while preserving its physical paths and declared names.
    /// </summary>
    /// <param name="destination">The non-local tuple location.</param>
    /// <param name="numberName">The declared numeric tuple element name.</param>
    /// <param name="textName">The declared reference tuple element name.</param>
    /// <param name="initialText">The original referenced string.</param>
    [TestMethod]
    [DataRow("tupleArgument", "ArgumentNumber", "ArgumentText", "argument")]
    [DataRow("localObject.Pair", "Code", "Label", "answer!")]
    [DataRow("localTupleArray[0]", "Number", "Text", "answer")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DefaultAssignmentPreservesStructuredDestinationIdentity(
        string destination, string numberName, string textName, string initialText)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement original = await ReadEvaluationAsync(client, frameId, destination, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual($"(42, \"{initialText}\")", original.GetProperty("result").GetString());
            await AssertStructAssignmentTupleChildrenAsync(client, original, destination,
                numberName, textName, "42", $"\"{initialText}\"").ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(client, frameId, destination, "default", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("(0, null)", assigned.GetProperty("value").GetString());
            await AssertStructAssignmentTupleChildrenAsync(client, assigned, destination,
                numberName, textName, "0", "null").ConfigureAwait(false);
            JsonElement refreshed = await ReadEvaluationAsync(client, frameId, destination, success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("(0, null)", refreshed.GetProperty("result").GetString());
            await AssertStructAssignmentTupleChildrenAsync(client, refreshed, destination,
                numberName, textName, "0", "null").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localTuple.Number", "42", "int")
                .ConfigureAwait(false);
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Clears a span without transferring a reference from another scope or changing its backing array.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DefaultAssignmentClearsRefLikeStorageWithoutCopyingReferences()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localSpan._length", "2", "int")
                .ConfigureAwait(false);
            JsonElement assigned = await ReadSetExpressionAsync(client, frameId, "localSpan", "default", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("System.Span<int>", assigned.GetProperty("type").GetString());
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localSpan._length", "0", "int")
                .ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localOtherSpan._length", "3", "int")
                .ConfigureAwait(false);
            JsonElement array = await ReadEvaluationAsync(client, frameId, "localArray", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement[] elements = await ReadVariablesAsync(client, array.GetProperty("variablesReference").GetInt32())
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(["41", "42", "43"], elements.Select(element => element.GetProperty("value").GetString()));
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Keeps C# null distinct from the default literal and Visual Basic's contextual Nothing conversion.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DefaultAssignmentDoesNotPermitCSharpNullIntoNonNullableValues()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (string Destination, string Message)[] rejections =
            [
                ("localNumber", "Assignment from 'object' to 'int' requires an explicit supported conversion."),
                ("localTuple", "Whole-value assignment requires existing unboxed value types; " +
                    "implicit boxing and unboxing are not supported.")
            ];
            foreach ((string destination, string message) in rejections)
            {
                JsonElement rejected = await ReadSetExpressionAsync(client, frameId, destination, "null", success: false,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(message, rejected.GetProperty("message").GetString());
            }

            await AssertStructAssignmentEvaluationAsync(client, frameId, "localNumber", "43", "int")
                .ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localTuple",
                "(42, \"answer\")", "(int Number, string Text)").ConfigureAwait(false);
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

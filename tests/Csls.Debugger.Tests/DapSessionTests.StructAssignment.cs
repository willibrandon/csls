using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies whole-value assignment through real managed tuple and nullable storage.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rejects different sixteen-byte runtime types without copying the decimal payload into a GUID.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentRejectsEqualSizedGuidAndDecimal()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localGuid",
                "00112233-4455-6677-8899-aabbccddeeff", "System.Guid").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localDecimal",
                "-1234.50", "decimal").ConfigureAwait(false);

            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, "localGuid", "localDecimal", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("identical loaded runtime types", message, StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localGuid",
                "00112233-4455-6677-8899-aabbccddeeff", "System.Guid").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(client, frameId, "localDecimal",
                "-1234.50", "decimal").ConfigureAwait(false);
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
    /// Rejects distinct generic arguments even when wrappers have equal sizes or identical absent payloads.
    /// </summary>
    /// <param name="destination">The signed integer wrapper receiving the attempted copy.</param>
    /// <param name="source">The unsigned integer wrapper supplying the incompatible value.</param>
    /// <param name="nullable">Whether both wrappers are absent nullable values.</param>
    /// <param name="destinationPayload">The exact original signed payload.</param>
    /// <param name="sourcePayload">The exact original unsigned payload.</param>
    [TestMethod]
    [DataRow("localEmptyNullable", "localEmptyUnsignedNullable", true, "0", "0")]
    [DataRow("localSingleTuple", "localUnsignedSingleTuple", false, "1", "17")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentRejectsEqualSizedGenericArguments(
        string destination,
        string source,
        bool nullable,
        string destinationPayload,
        string sourcePayload)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await AssertStructAssignmentGenericStorageAsync(
                client, frameId, destination, "int", destinationPayload, nullable)
                .ConfigureAwait(false);
            await AssertStructAssignmentGenericStorageAsync(
                client, frameId, source, "uint", sourcePayload, nullable)
                .ConfigureAwait(false);

            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, destination, source, success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("identical loaded runtime types", message, StringComparison.Ordinal);
            await AssertStructAssignmentGenericStorageAsync(
                client, frameId, destination, "int", destinationPayload, nullable)
                .ConfigureAwait(false);
            await AssertStructAssignmentGenericStorageAsync(
                client, frameId, source, "uint", sourcePayload, nullable)
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
    /// Rejects copying ref-like storage while preserving both spans and their underlying array.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentRejectsRefLikeStorage()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await AssertStructAssignmentEvaluationAsync(
                client, frameId, "localSpan._length", "2", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(
                client, frameId, "localOtherSpan._length", "3", "int").ConfigureAwait(false);

            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, "localSpan", "localOtherSpan", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("ref-like types", message, StringComparison.Ordinal);
            await AssertStructAssignmentEvaluationAsync(
                client, frameId, "localSpan._length", "2", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(
                client, frameId, "localOtherSpan._length", "3", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(
                client, frameId, "localArray[0]", "41", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(
                client, frameId, "localArray[1]", "42", "int").ConfigureAwait(false);
            await AssertStructAssignmentEvaluationAsync(
                client, frameId, "localArray[2]", "43", "int").ConfigureAwait(false);
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
    /// Rejects implicit unboxing and different whole-value runtime types before changing tuple storage.
    /// </summary>
    /// <param name="sourceExpression">The boxed or differently constructed source tuple.</param>
    /// <param name="sourceValue">The exact tuple contents that must survive rejection.</param>
    /// <param name="sourceType">The source tuple's authored debugger type.</param>
    /// <param name="reason">The specific unsupported-copy category reported by the debugger.</param>
    [TestMethod]
    [DataRow("localBoxedTuple", "(10, \"ten\")", "(int, string)", "unboxed value types")]
    [DataRow("localLongTuple", "(1, 2, 3, 4, 5, 6, 7, 8, 9)",
        "(int One, int Two, int Three, int Four, int Five, int Six, int Seven, int Eight, int Nine)",
        "identical loaded runtime types")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentRejectsBoxedOrDifferentRuntimeValues(
        string sourceExpression,
        string sourceValue,
        string sourceType,
        string reason)
    {
        const string Destination = "localTuple";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement before = await ReadEvaluationAsync(
                client, frameId, Destination, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(42, \"answer\")", before.GetProperty("result").GetString());
            await AssertStructAssignmentTupleChildrenAsync(
                client, before, Destination, "Number", "Text", "42", "\"answer\"")
                .ConfigureAwait(false);
            JsonElement source = await ReadEvaluationAsync(
                client, frameId, sourceExpression, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(sourceValue, source.GetProperty("result").GetString());
            Assert.AreEqual(sourceType, source.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, source.GetProperty("variablesReference").GetInt32());

            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, Destination, sourceExpression, success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains(reason, message, StringComparison.Ordinal);
            JsonElement unchanged = await ReadEvaluationAsync(
                client, frameId, Destination, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(42, \"answer\")", unchanged.GetProperty("result").GetString());
            await AssertStructAssignmentTupleChildrenAsync(
                client, unchanged, Destination, "Number", "Text", "42", "\"answer\"")
                .ConfigureAwait(false);
            JsonElement unchangedSource = await ReadEvaluationAsync(
                client, frameId, sourceExpression, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(sourceValue, unchangedSource.GetProperty("result").GetString());
            Assert.AreEqual(sourceType, unchangedSource.GetProperty("type").GetString());
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
    /// Reports unsupported casts through evaluation and both assignment commands without hanging the session.
    /// </summary>
    /// <param name="command">The DAP operation asked to process the unsupported conversion.</param>
    [TestMethod]
    [DataRow("setExpression")]
    [DataRow("setVariable")]
    [DataRow("evaluate")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentRejectsUnsupportedConversionWithoutHanging(string command)
    {
        const string UnsupportedValue = "(System.Int128)number";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (_, int localsReference) = await ReadFrameScopeReferencesAsync(client, frameId)
                .ConfigureAwait(false);
            JsonElement rejected;
            try
            {
                rejected = command switch
                {
                    "setVariable" => await ReadSetVariableAsync(
                        client, localsReference, "localNumber", UnsupportedValue, success: false,
                        TestContext.CancellationToken).ConfigureAwait(false),
                    "setExpression" => await ReadSetExpressionAsync(
                        client, frameId, "localNumber", UnsupportedValue, success: false,
                        TestContext.CancellationToken).ConfigureAwait(false),
                    "evaluate" => await ReadEvaluationAsync(
                        client, frameId, UnsupportedValue, success: false,
                        TestContext.CancellationToken).ConfigureAwait(false),
                    _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown DAP operation.")
                };
            }
            catch
            {
                WriteTargetCodeFailureDiagnostics(client);
                throw;
            }

            Assert.AreEqual("The type operation cannot convert this value to 'System.Int128' without supported value materialization.",
                rejected.GetProperty("message").GetString());
            JsonElement unchanged = await ReadEvaluationAsync(
                client, frameId, "localNumber", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("43", unchanged.GetProperty("result").GetString());
            Assert.AreEqual("int", unchanged.GetProperty("type").GetString());
            JsonElement source = await ReadEvaluationAsync(
                client, frameId, "number", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("42", source.GetProperty("result").GetString());
            Assert.AreEqual("int", source.GetProperty("type").GetString());

            JsonElement assigned = command == "setVariable"
                ? await ReadSetVariableAsync(
                    client, localsReference, "localNumber", "44", success: true,
                    TestContext.CancellationToken).ConfigureAwait(false)
                : await ReadSetExpressionAsync(
                    client, frameId, "localNumber", "44", success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("44", assigned.GetProperty("value").GetString());
            Assert.AreEqual("int", assigned.GetProperty("type").GetString());
            Assert.AreEqual(0, assigned.GetProperty("variablesReference").GetInt32());
            JsonElement updated = await ReadEvaluationAsync(
                client, frameId, "localNumber", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("44", updated.GetProperty("result").GetString());
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
    /// Copies a tuple into independent local, argument, or array storage while preserving destination names.
    /// </summary>
    /// <param name="destination">The writable source expression for the destination tuple.</param>
    /// <param name="numberName">The destination's authored numeric element name.</param>
    /// <param name="textName">The destination's authored string element name.</param>
    /// <param name="initialText">The string retained by the destination before the whole copy.</param>
    [TestMethod]
    [DataRow("localTuple", "Number", "Text", "answer")]
    [DataRow("tupleArgument", "ArgumentNumber", "ArgumentText", "argument")]
    [DataRow("localTupleArray[0]", "Number", "Text", "answer")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentCopiesTuplesWithDestinationNames(
        string destination,
        string numberName,
        string textName,
        string initialText)
    {
        const string Source = "localObject.Pair";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement initialized = await ReadSetExpressionAsync(
                client, frameId, destination + "." + numberName, "31", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("31", initialized.GetProperty("value").GetString());
            Assert.AreEqual("int", initialized.GetProperty("type").GetString());

            JsonElement before = await ReadEvaluationAsync(
                client, frameId, destination, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual($"(31, \"{initialText}\")", before.GetProperty("result").GetString());
            JsonElement source = await ReadEvaluationAsync(
                client, frameId, Source, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(42, \"answer!\")", source.GetProperty("result").GetString());
            await AssertStructAssignmentTupleChildrenAsync(
                client, source, Source, "Code", "Label", "42", "\"answer!\"")
                .ConfigureAwait(false);

            TestContext.WriteLine($"Verified tuple preconditions; assigning {Source} to {destination}.");
            JsonElement assignment;
            try
            {
                assignment = await ReadSetExpressionAsync(
                    client, frameId, destination, Source, success: true, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                WriteTargetCodeFailureDiagnostics(client);
                throw;
            }
            Assert.AreEqual("(42, \"answer!\")", assignment.GetProperty("value").GetString());
            await AssertStructAssignmentTupleChildrenAsync(
                client, assignment, destination, numberName, textName, "42", "\"answer!\"")
                .ConfigureAwait(false);
            JsonElement copied = await ReadEvaluationAsync(
                client, frameId, destination, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(42, \"answer!\")", copied.GetProperty("result").GetString());
            await AssertStructAssignmentTupleChildrenAsync(
                client, copied, destination, numberName, textName, "42", "\"answer!\"")
                .ConfigureAwait(false);

            JsonElement changed = await ReadSetExpressionAsync(
                client, frameId, destination + "." + numberName, "87", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("87", changed.GetProperty("value").GetString());
            JsonElement independent = await ReadEvaluationAsync(
                client, frameId, destination, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(87, \"answer!\")", independent.GetProperty("result").GetString());
            JsonElement unchangedSource = await ReadEvaluationAsync(
                client, frameId, Source, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(42, \"answer!\")", unchangedSource.GetProperty("result").GetString());
            await AssertStructAssignmentTupleChildrenAsync(
                client, unchangedSource, Source, "Code", "Label", "42", "\"answer!\"")
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
    /// Copies the complete nullable wrapper in both directions without losing absent-source identity.
    /// </summary>
    /// <param name="destination">The nullable local receiving the copy.</param>
    /// <param name="source">The same-type nullable local supplying the complete wrapper.</param>
    /// <param name="sourceHasValue">Whether the source wrapper contains the fixture integer.</param>
    [TestMethod]
    [DataRow("localEmptyNullable", "localNullable", true)]
    [DataRow("localNullable", "localEmptyNullable", false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentCopiesNullablePresence(
        string destination,
        string source,
        bool sourceHasValue)
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement before = await ReadEvaluationAsync(
                client, frameId, destination, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(sourceHasValue ? "null" : "45", before.GetProperty("result").GetString());
            await AssertStructAssignmentNullableChildrenAsync(client, before, !sourceHasValue)
                .ConfigureAwait(false);
            JsonElement originalSource = await ReadEvaluationAsync(
                client, frameId, source, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(sourceHasValue ? "45" : "null", originalSource.GetProperty("result").GetString());
            await AssertStructAssignmentNullableChildrenAsync(client, originalSource, sourceHasValue)
                .ConfigureAwait(false);

            TestContext.WriteLine($"Verified nullable preconditions; assigning {source} to {destination}.");
            JsonElement assignment;
            try
            {
                assignment = await ReadSetExpressionAsync(
                    client, frameId, destination, source, success: true, TestContext.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                WriteTargetCodeFailureDiagnostics(client);
                throw;
            }
            Assert.AreEqual(sourceHasValue ? "45" : "null", assignment.GetProperty("value").GetString());
            await AssertStructAssignmentNullableChildrenAsync(client, assignment, sourceHasValue)
                .ConfigureAwait(false);
            JsonElement copied = await ReadEvaluationAsync(
                client, frameId, destination, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(sourceHasValue ? "45" : "null", copied.GetProperty("result").GetString());
            await AssertStructAssignmentNullableChildrenAsync(client, copied, sourceHasValue)
                .ConfigureAwait(false);
            JsonElement unchangedSource = await ReadEvaluationAsync(
                client, frameId, source, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(sourceHasValue ? "45" : "null", unchangedSource.GetProperty("result").GetString());
            await AssertStructAssignmentNullableChildrenAsync(client, unchangedSource, sourceHasValue)
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

    private async Task AssertStructAssignmentEvaluationAsync(
        DapTestClient client,
        int frameId,
        string expression,
        string expectedValue,
        string expectedType)
    {
        JsonElement value = await ReadEvaluationAsync(
            client, frameId, expression, success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(expectedValue, value.GetProperty("result").GetString(), expression);
        Assert.AreEqual(expectedType, value.GetProperty("type").GetString(), expression);
    }

    private async Task AssertStructAssignmentGenericStorageAsync(
        DapTestClient client,
        int frameId,
        string expression,
        string elementType,
        string expectedPayload,
        bool nullable)
    {
        JsonElement value = await ReadEvaluationAsync(
            client, frameId, expression, success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        string expectedType = nullable ? elementType + "?" : $"System.ValueTuple<{elementType}>";
        Assert.AreEqual(expectedType, value.GetProperty("type").GetString(), expression);
        Assert.AreEqual(nullable ? "null" : "{" + expectedType + "}",
            value.GetProperty("result").GetString(), expression);
        int reference = value.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        JsonElement payload = Assert.ContainsSingle(children.Where(child =>
            child.GetProperty("name").GetString() == (nullable ? "value" : "Item1")));
        Assert.AreEqual(expectedPayload, payload.GetProperty("value").GetString(), expression);
        Assert.AreEqual(elementType, payload.GetProperty("type").GetString(), expression);
        Assert.AreEqual(0, payload.GetProperty("variablesReference").GetInt32());
        if (nullable)
        {
            JsonElement presence = Assert.ContainsSingle(children.Where(child =>
                child.GetProperty("name").GetString() == "hasValue"));
            Assert.AreEqual("false", presence.GetProperty("value").GetString(), expression);
            Assert.AreEqual("bool", presence.GetProperty("type").GetString(), expression);
            Assert.AreEqual(0, presence.GetProperty("variablesReference").GetInt32());
        }
    }

    private async Task AssertStructAssignmentTupleChildrenAsync(
        DapTestClient client,
        JsonElement tuple,
        string expression,
        string numberName,
        string textName,
        string number,
        string text)
    {
        Assert.AreEqual($"(int {numberName}, string {textName})", tuple.GetProperty("type").GetString());
        int reference = tuple.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        Assert.AreSequenceEqual(
            [numberName, textName, "Raw View"],
            children.Select(child => child.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(number, children[0].GetProperty("value").GetString());
        Assert.AreEqual("int", children[0].GetProperty("type").GetString());
        Assert.AreEqual(0, children[0].GetProperty("variablesReference").GetInt32());
        Assert.AreEqual(expression + ".Item1", children[0].GetProperty("evaluateName").GetString());
        Assert.AreEqual(text, children[1].GetProperty("value").GetString());
        Assert.AreEqual("string", children[1].GetProperty("type").GetString());
        Assert.AreEqual(0, children[1].GetProperty("variablesReference").GetInt32());
        Assert.AreEqual(expression + ".Item2", children[1].GetProperty("evaluateName").GetString());
    }

    private async Task AssertStructAssignmentNullableChildrenAsync(
        DapTestClient client,
        JsonElement nullable,
        bool hasValue)
    {
        Assert.AreEqual("int?", nullable.GetProperty("type").GetString());
        int reference = nullable.GetProperty("variablesReference").GetInt32();
        Assert.IsGreaterThan(0, reference);
        JsonElement[] children = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
        JsonElement presence = Assert.ContainsSingle(children.Where(child =>
            child.GetProperty("name").GetString() == "hasValue"));
        Assert.AreEqual(hasValue ? "true" : "false", presence.GetProperty("value").GetString());
        Assert.AreEqual("bool", presence.GetProperty("type").GetString());
        Assert.AreEqual(0, presence.GetProperty("variablesReference").GetInt32());
        JsonElement payload = Assert.ContainsSingle(children.Where(child =>
            child.GetProperty("name").GetString() == "value"));
        Assert.AreEqual(hasValue ? "45" : "0", payload.GetProperty("value").GetString());
        Assert.AreEqual("int", payload.GetProperty("type").GetString());
        Assert.AreEqual(0, payload.GetProperty("variablesReference").GetInt32());
    }
}

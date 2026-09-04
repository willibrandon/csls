using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies stopped-state managed thread, stack, scope, and variable inspection.
/// </summary>
public sealed partial class DapSessionTests
{
    private static readonly string[] s_variableInvalidationAreas = ["variables"];
    private static readonly string[] s_targetCodeInvalidationAreas = ["stacks", "variables"];
    private static readonly string[] s_rawTupleFieldNames =
        ["Item1", "Item2", "Item3", "Item4", "Item5", "Item6", "Item7", "Rest"];

    /// <summary>
    /// Pauses a live managed target, enumerates real runtime threads, and resumes execution.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedPauseEnumeratesThreadsAndContinuesTarget()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-pause-{Guid.NewGuid():N}.signal");
        try
        {
            string fixtureSourcePath = Path.Join(
                FindRepositoryRoot(),
                "tests",
                "Csls.TestProcessHost",
                "DebuggerFixture.cs");
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "initialize",
                WriteVariablePagingInitializeArguments,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsTrue(
                initialize.RootElement
                    .GetProperty("body")
                    .GetProperty("supportsSetVariable")
                    .GetBoolean());
            Assert.IsTrue(
                initialize.RootElement
                    .GetProperty("body")
                    .GetProperty("supportsSetExpression")
                    .GetBoolean());
            Assert.IsTrue(
                initialize.RootElement
                    .GetProperty("body")
                    .GetProperty("supportsCompletionsRequest")
                    .GetBoolean());
            _ = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-fixture", waitPath],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            _ = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument configuration = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument launch = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument process = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument ready = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(ready.RootElement, "output");
            Assert.AreEqual(
                "ready",
                ready.RootElement.GetProperty("body").GetProperty("output").GetString());

            int pauseSequence = await client.SendRequestAsync(
                "pause",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stopped = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(stopped.RootElement, "stopped");
            Assert.AreEqual(
                "pause",
                stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
            Assert.IsTrue(
                stopped.RootElement.GetProperty("body").GetProperty("allThreadsStopped").GetBoolean());
            using JsonDocument pause = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(pause.RootElement, pauseSequence, "pause", success: true);

            int threadsSequence = await client.SendRequestAsync(
                "threads",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument threads = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
            JsonElement[] threadItems = [.. threads.RootElement
                .GetProperty("body")
                .GetProperty("threads")
                .EnumerateArray()];
            Assert.IsNotEmpty(threadItems);
            Assert.IsTrue(threadItems.All(thread => thread.GetProperty("id").GetInt32() > 0));
            Assert.HasCount(
                threadItems.Length,
                threadItems.Select(thread => thread.GetProperty("id").GetInt32()).Distinct().ToArray());

            int fixtureFrameId = 0;
            foreach (int threadId in threadItems.Select(thread =>
                thread.GetProperty("id").GetInt32()))
            {
                int stackSequence = await client.SendRequestAsync(
                    "stackTrace",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("threadId", threadId);
                        writer.WriteNumber("startFrame", 0);
                        writer.WriteNumber("levels", 64);
                        writer.WriteEndObject();
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument stack = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(stack.RootElement, stackSequence, "stackTrace", success: true);
                JsonElement[] frames = [.. stack.RootElement
                    .GetProperty("body")
                    .GetProperty("stackFrames")
                    .EnumerateArray()];
                Assert.IsGreaterThanOrEqualTo(
                    frames.Length,
                    stack.RootElement.GetProperty("body").GetProperty("totalFrames").GetInt32());
                JsonElement fixtureFrame = frames.FirstOrDefault(frame =>
                    frame.TryGetProperty("source", out JsonElement source) &&
                    source.TryGetProperty("path", out JsonElement sourcePath) &&
                    DebuggerTestPath.AreEquivalent(
                        sourcePath.GetString(),
                        fixtureSourcePath) &&
                    frame.GetProperty("line").GetInt32() > 0);
                if (fixtureFrame.ValueKind != JsonValueKind.Undefined)
                {
                    fixtureFrameId = fixtureFrame.GetProperty("id").GetInt32();
                    break;
                }
            }

            Assert.IsGreaterThan(0, fixtureFrameId, "No managed stack frame resolved to the fixture source.");

            int scopesSequence = await client.SendRequestAsync(
                "scopes",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("frameId", fixtureFrameId);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument scopes = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
            JsonElement[] scopeItems = [.. scopes.RootElement
                .GetProperty("body")
                .GetProperty("scopes")
                .EnumerateArray()];
            Assert.HasCount(2, scopeItems);
            int staleVariablesReference = scopeItems.Single(scope =>
                    scope.GetProperty("name").GetString() == "Arguments")
                .GetProperty("variablesReference")
                .GetInt32();
            int localVariablesReference = scopeItems.Single(scope =>
                    scope.GetProperty("name").GetString() == "Locals")
                .GetProperty("variablesReference")
                .GetInt32();

            Dictionary<string, JsonElement[]> variablesByScope = new(StringComparer.Ordinal);
            foreach (JsonElement scope in scopeItems)
            {
                int reference = scope.GetProperty("variablesReference").GetInt32();
                int variablesSequence = await client.SendRequestAsync(
                    "variables",
                    writer =>
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("variablesReference", reference);
                        writer.WriteEndObject();
                    },
                    TestContext.CancellationToken).ConfigureAwait(false);
                using JsonDocument variables = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertResponse(variables.RootElement, variablesSequence, "variables", success: true);
                variablesByScope.Add(
                    scope.GetProperty("name").GetString()!,
                    [.. variables.RootElement
                        .GetProperty("body")
                        .GetProperty("variables")
                        .EnumerateArray()
                        .Select(variable => variable.Clone())]);
            }

            JsonElement[] arguments = variablesByScope["Arguments"];
            Dictionary<string, JsonElement> argumentsByName = arguments.ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("42", argumentsByName["number"].GetProperty("value").GetString());
            Assert.AreEqual("int", argumentsByName["number"].GetProperty("type").GetString());
            Assert.AreEqual(
                "\"answer\"",
                argumentsByName["text"].GetProperty("value").GetString());
            Assert.AreEqual("string", argumentsByName["text"].GetProperty("type").GetString());
            JsonElement[] locals = variablesByScope["Locals"];
            Dictionary<string, JsonElement> localsByName = locals.ToDictionary(
                variable => variable.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("43", localsByName["localNumber"].GetProperty("value").GetString());
            Assert.AreEqual(
                "localNumber",
                localsByName["localNumber"].GetProperty("evaluateName").GetString());
            Assert.AreEqual("44", localsByName["localLong"].GetProperty("value").GetString());
            Assert.AreEqual("long", localsByName["localLong"].GetProperty("type").GetString());
            Assert.AreEqual("1", localsByName["localByte"].GetProperty("value").GetString());
            Assert.AreEqual("byte", localsByName["localByte"].GetProperty("type").GetString());
            Assert.AreEqual(
                "\"answer!\"",
                localsByName["localText"].GetProperty("value").GetString());
            JsonElement localArray = localsByName["localArray"];
            Assert.AreEqual("{int[3]}", localArray.GetProperty("value").GetString());
            Assert.AreEqual("int[]", localArray.GetProperty("type").GetString());
            int arrayReference = localArray.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, arrayReference);
            JsonElement[] arrayElements = await ReadVariablesAsync(client, arrayReference)
                .ConfigureAwait(false);
            Assert.HasCount(3, arrayElements);
            Assert.AreEqual("[0]", arrayElements[0].GetProperty("name").GetString());
            Assert.AreEqual("41", arrayElements[0].GetProperty("value").GetString());
            Assert.AreEqual(
                "localArray[0]",
                arrayElements[0].GetProperty("evaluateName").GetString());
            Assert.AreEqual("[2]", arrayElements[2].GetProperty("name").GetString());
            Assert.AreEqual("43", arrayElements[2].GetProperty("value").GetString());

            JsonElement localObject = localsByName["localObject"];
            Assert.AreEqual(
                "{Csls.TestProcessHost.DebuggerFixtureValue}",
                localObject.GetProperty("value").GetString());
            Assert.AreEqual(
                "Csls.TestProcessHost.DebuggerFixtureValue",
                localObject.GetProperty("type").GetString());
            int objectReference = localObject.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, objectReference);
            JsonElement[] fields = await ReadVariablesAsync(client, objectReference)
                .ConfigureAwait(false);
            Dictionary<string, JsonElement> fieldsByName = fields.ToDictionary(
                field => field.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("42", fieldsByName["Number"].GetProperty("value").GetString());
            Assert.AreEqual(
                "localObject.Number",
                fieldsByName["Number"].GetProperty("evaluateName").GetString());
            Assert.AreEqual(
                "\"answer!\"",
                fieldsByName["Text"].GetProperty("value").GetString());

            JsonElement localList = localsByName["localList"];
            Assert.AreEqual(
                "Csls.TestProcessHost.DebuggerFixtureList",
                localList.GetProperty("type").GetString());
            int listReference = localList.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, listReference);
            JsonElement[] listFields = await ReadVariablesAsync(client, listReference)
                .ConfigureAwait(false);
            Dictionary<string, JsonElement> listFieldsByName = listFields.ToDictionary(
                field => field.GetProperty("name").GetString()!,
                StringComparer.Ordinal);
            Assert.AreEqual("1", listFieldsByName["_size"].GetProperty("value").GetString());
            Assert.AreEqual(
                "localList._size",
                listFieldsByName["_size"].GetProperty("evaluateName").GetString());
            JsonElement[] inheritedFieldCompletions = await ReadCompletionsAsync(
                client,
                fixtureFrameId,
                "localList._s",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "_size",
                inheritedFieldCompletions.Select(completion =>
                    completion.GetProperty("label").GetString()!));

            JsonElement localNullable = localsByName["localNullable"];
            Assert.AreEqual("45", localNullable.GetProperty("value").GetString());
            Assert.AreEqual("int?", localNullable.GetProperty("type").GetString());
            JsonElement localEmptyNullable = localsByName["localEmptyNullable"];
            Assert.AreEqual("null", localEmptyNullable.GetProperty("value").GetString());
            Assert.AreEqual("int?", localEmptyNullable.GetProperty("type").GetString());
            JsonElement localTuple = localsByName["localTuple"];
            Assert.AreEqual("(42, \"answer\")", localTuple.GetProperty("value").GetString());
            Assert.AreEqual(
                "(int Number, string Text)",
                localTuple.GetProperty("type").GetString());
            int tupleReference = localTuple.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, tupleReference);
            Dictionary<string, JsonElement> tupleFields = (await ReadVariablesAsync(
                client,
                tupleReference).ConfigureAwait(false)).ToDictionary(
                    field => field.GetProperty("name").GetString()!,
                    StringComparer.Ordinal);
            Assert.AreEqual("42", tupleFields["Number"].GetProperty("value").GetString());
            Assert.AreEqual(
                "\"answer\"",
                tupleFields["Text"].GetProperty("value").GetString());
            JsonElement localBoxedTuple = localsByName["localBoxedTuple"];
            Assert.AreEqual(
                "(10, \"ten\")",
                localBoxedTuple.GetProperty("value").GetString());
            Assert.AreEqual(
                "(int, string)",
                localBoxedTuple.GetProperty("type").GetString());
            Assert.IsGreaterThan(
                0,
                localBoxedTuple.GetProperty("variablesReference").GetInt32());
            Assert.AreSequenceEqual(
                ["Item1", "Item2"],
                (await ReadVariablesAsync(
                    client,
                    localBoxedTuple.GetProperty("variablesReference").GetInt32())
                    .ConfigureAwait(false))
                    .Select(field => field.GetProperty("name").GetString())
                    .ToArray());
            JsonElement localLongTuple = localsByName["localLongTuple"];
            Assert.AreEqual(
                "(1, 2, 3, 4, 5, 6, 7, 8, 9)",
                localLongTuple.GetProperty("value").GetString());
            Assert.AreEqual(
                "(int One, int Two, int Three, int Four, int Five, int Six, int Seven, int Eight, int Nine)",
                localLongTuple.GetProperty("type").GetString());
            int longTupleReference = localLongTuple
                .GetProperty("variablesReference")
                .GetInt32();
            JsonElement[] longTuplePage = await ReadVariablesAsync(
                client,
                longTupleReference,
                start: 7,
                count: 2).ConfigureAwait(false);
            Assert.HasCount(2, longTuplePage);
            Assert.AreEqual("Eight", longTuplePage[0].GetProperty("name").GetString());
            Assert.AreEqual("8", longTuplePage[0].GetProperty("value").GetString());
            Assert.AreEqual(
                "localLongTuple.Rest.Item1",
                longTuplePage[0].GetProperty("evaluateName").GetString());
            Assert.AreEqual("Nine", longTuplePage[1].GetProperty("name").GetString());
            Assert.AreEqual("9", longTuplePage[1].GetProperty("value").GetString());
            Assert.AreEqual(
                "localLongTuple.Rest.Item2",
                longTuplePage[1].GetProperty("evaluateName").GetString());
            JsonElement[] longTupleChildren = await ReadVariablesAsync(
                client,
                longTupleReference).ConfigureAwait(false);
            Assert.HasCount(10, longTupleChildren);
            JsonElement rawTuple = longTupleChildren[^1];
            Assert.AreEqual("Raw View", rawTuple.GetProperty("name").GetString());
            int rawTupleReference = rawTuple.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, rawTupleReference);
            string[] rawTupleFieldNames =
            [
                .. (await ReadVariablesAsync(client, rawTupleReference).ConfigureAwait(false))
                    .Select(field => field.GetProperty("name").GetString()!)
            ];
            Assert.AreSequenceEqual(s_rawTupleFieldNames, rawTupleFieldNames);
            JsonElement localSingleTuple = localsByName["localSingleTuple"];
            Assert.AreEqual(
                "{System.ValueTuple<int>}",
                localSingleTuple.GetProperty("value").GetString());
            Assert.AreEqual(
                "System.ValueTuple<int>",
                localSingleTuple.GetProperty("type").GetString());
            JsonElement localNonTuple = localsByName["localNonTuple"];
            Assert.AreEqual(
                "{System.ValueTuple<int, int, int, int, int, int, int, int>}",
                localNonTuple.GetProperty("value").GetString());
            Assert.AreEqual(
                "System.ValueTuple<int, int, int, int, int, int, int, int>",
                localNonTuple.GetProperty("type").GetString());
            JsonElement localMode = localsByName["localMode"];
            Assert.AreEqual("Second", localMode.GetProperty("value").GetString());
            Assert.AreEqual(
                "Csls.TestProcessHost.DebuggerFixtureMode",
                localMode.GetProperty("type").GetString());
            Assert.AreEqual(
                "7",
                localsByName["localUnknownMode"].GetProperty("value").GetString());
            Assert.AreEqual(
                "Read | Execute",
                localsByName["localOptions"].GetProperty("value").GetString());
            Assert.AreEqual(
                "-1234.50",
                localsByName["localDecimal"].GetProperty("value").GetString());
            Assert.AreEqual(
                "decimal",
                localsByName["localDecimal"].GetProperty("type").GetString());
            Assert.AreEqual(
                "2026-09-03T12:34:56.7890123Z",
                localsByName["localDateTime"].GetProperty("value").GetString());
            Assert.AreEqual(
                "System.DateTime",
                localsByName["localDateTime"].GetProperty("type").GetString());
            Assert.AreEqual(
                "2026-09-03T12:34:56.0000000",
                localsByName["localUnspecifiedDateTime"].GetProperty("value").GetString());
            Assert.AreEqual(
                "2026-09-03T12:34:56.0000000 (Local)",
                localsByName["localLocalDateTime"].GetProperty("value").GetString());
            Assert.AreEqual(
                "1.02:03:04.5000000",
                localsByName["localTimeSpan"].GetProperty("value").GetString());
            Assert.AreEqual(
                "System.TimeSpan",
                localsByName["localTimeSpan"].GetProperty("type").GetString());
            Assert.AreEqual(
                "00112233-4455-6677-8899-aabbccddeeff",
                localsByName["localGuid"].GetProperty("value").GetString());
            Assert.AreEqual(
                "System.Guid",
                localsByName["localGuid"].GetProperty("type").GetString());
            Assert.AreEqual(
                "2026-09-03T12:34:56.0000000-07:00",
                localsByName["localDateTimeOffset"].GetProperty("value").GetString());
            Assert.AreEqual(
                "System.DateTimeOffset",
                localsByName["localDateTimeOffset"].GetProperty("type").GetString());
            Assert.AreEqual(
                "\"a\\0\\a\\b\\f\\v\\\\\\\"\\u0001😀\"",
                localsByName["localEscapedText"].GetProperty("value").GetString());
            Assert.AreEqual(
                "'\\n'",
                localsByName["localEscapedCharacter"].GetProperty("value").GetString());

            JsonElement evaluatedLocal = await ReadEvaluationAsync(
                client,
                fixtureFrameId,
                "localNumber",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("43", evaluatedLocal.GetProperty("result").GetString());
            Assert.AreEqual("int", evaluatedLocal.GetProperty("type").GetString());
            Assert.AreEqual(0, evaluatedLocal.GetProperty("variablesReference").GetInt32());

            JsonElement evaluatedField = await ReadEvaluationAsync(
                client,
                fixtureFrameId,
                "localObject.Number",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", evaluatedField.GetProperty("result").GetString());

            JsonElement evaluatedArrayElement = await ReadEvaluationAsync(
                client,
                fixtureFrameId,
                "localArray[1]",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", evaluatedArrayElement.GetProperty("result").GetString());

            JsonElement evaluatedArithmetic = await ReadEvaluationAsync(
                client,
                fixtureFrameId,
                "localNumber + 1",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("44", evaluatedArithmetic.GetProperty("result").GetString());
            Assert.AreEqual("int", evaluatedArithmetic.GetProperty("type").GetString());

            JsonElement[] rootCompletions = await ReadCompletionsAsync(
                client,
                fixtureFrameId,
                "localN",
                TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement localNumberCompletion = rootCompletions.Single(completion =>
                completion.GetProperty("label").GetString() == "localNumber");
            Assert.AreEqual("variable", localNumberCompletion.GetProperty("type").GetString());
            Assert.AreEqual("int", localNumberCompletion.GetProperty("detail").GetString());
            Assert.AreEqual(1, localNumberCompletion.GetProperty("start").GetInt32());
            Assert.AreEqual(6, localNumberCompletion.GetProperty("length").GetInt32());

            JsonElement[] memberCompletions = await ReadCompletionsAsync(
                client,
                fixtureFrameId,
                "localObject.N",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "Number",
                memberCompletions.Select(completion =>
                    completion.GetProperty("label").GetString()!));
            Assert.Contains(
                "NextNumber",
                memberCompletions.Select(completion =>
                    completion.GetProperty("label").GetString()!));
            Assert.AreEqual(
                "field",
                memberCompletions.Single(completion =>
                        completion.GetProperty("label").GetString() == "Number")
                    .GetProperty("type")
                    .GetString());

            JsonElement[] staticCompletions = await ReadCompletionsAsync(
                client,
                fixtureFrameId,
                "System.Math.A",
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "method",
                staticCompletions.First(completion =>
                        completion.GetProperty("label").GetString() == "Abs")
                    .GetProperty("type")
                    .GetString());

            JsonElement evaluatedConditional = await ReadEvaluationAsync(
                client,
                fixtureFrameId,
                "localNumber > 40 ? localArray[localNumber - 42] : 0",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("42", evaluatedConditional.GetProperty("result").GetString());

            JsonElement assignedLocal = await ReadSetVariableAsync(
                client,
                localVariablesReference,
                "localNumber",
                "localObject.Number + 2",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("44", assignedLocal.GetProperty("value").GetString());
            Assert.AreEqual("int", assignedLocal.GetProperty("type").GetString());

            JsonElement assignedWidenedLocal = await ReadSetVariableAsync(
                client,
                localVariablesReference,
                "localLong",
                "localNumber + 4",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("48", assignedWidenedLocal.GetProperty("value").GetString());
            Assert.AreEqual("long", assignedWidenedLocal.GetProperty("type").GetString());

            JsonElement assignedContextualLiteral = await ReadSetVariableAsync(
                client,
                localVariablesReference,
                "localByte",
                "255",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("255", assignedContextualLiteral.GetProperty("value").GetString());
            Assert.AreEqual("byte", assignedContextualLiteral.GetProperty("type").GetString());

            JsonElement overflowingContextualLiteral = await ReadSetVariableAsync(
                client,
                localVariablesReference,
                "localByte",
                "256",
                success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "outside the range",
                overflowingContextualLiteral.GetProperty("message").GetString()!,
                StringComparison.Ordinal);

            JsonElement assignedField = await ReadSetExpressionAsync(
                client,
                fixtureFrameId,
                "localObject.Number",
                "localNumber + 1",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("45", assignedField.GetProperty("value").GetString());

            JsonElement assignedInheritedField = await ReadSetExpressionAsync(
                client,
                fixtureFrameId,
                "localList._size",
                "2",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("2", assignedInheritedField.GetProperty("value").GetString());

            JsonElement assignedElement = await ReadSetVariableAsync(
                client,
                arrayReference,
                "[1]",
                "localObject.Number + 1",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("46", assignedElement.GetProperty("value").GetString());

            JsonElement assignedArgument = await ReadSetVariableAsync(
                client,
                staleVariablesReference,
                "number",
                "47",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("47", assignedArgument.GetProperty("value").GetString());

            JsonElement assignedReference = await ReadSetExpressionAsync(
                client,
                fixtureFrameId,
                "localObject.Text",
                "text",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "\"answer\"",
                assignedReference.GetProperty("value").GetString());

            JsonElement unsafeStringMaterialization = await ReadSetExpressionAsync(
                client,
                fixtureFrameId,
                "localObject.Text",
                "\"changed\"",
                success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.Contains(
                "garbage-collection-unsafe point",
                unsafeStringMaterialization.GetProperty("message").GetString()!,
                StringComparison.Ordinal);

            JsonElement assignedNull = await ReadSetExpressionAsync(
                client,
                fixtureFrameId,
                "localObject.Text",
                "null",
                success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("null", assignedNull.GetProperty("value").GetString());

            Assert.AreEqual(
                "44",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "localNumber",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "45",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "localObject.Number",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "46",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "localArray[1]",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "47",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "number",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "48",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "localLong",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "255",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "localByte",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "2",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "localList._size",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());
            Assert.AreEqual(
                "null",
                (await ReadEvaluationAsync(
                    client,
                    fixtureFrameId,
                    "localObject.Text",
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false))
                    .GetProperty("result")
                    .GetString());

            int continueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument continued = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(continued.RootElement, "continued");
            using JsonDocument continueResponse = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                continueResponse.RootElement,
                continueSequence,
                "continue",
                success: true);

            int secondPauseSequence = await client.SendRequestAsync(
                "pause",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument secondStopped = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(secondStopped.RootElement, "stopped");
            using JsonDocument secondPause = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(secondPause.RootElement, secondPauseSequence, "pause", success: true);

            int staleVariablesSequence = await client.SendRequestAsync(
                "variables",
                writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("variablesReference", staleVariablesReference);
                    writer.WriteEndObject();
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument staleVariables = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                staleVariables.RootElement,
                staleVariablesSequence,
                "variables",
                success: false);
            Assert.Contains(
                "stale",
                staleVariables.RootElement.GetProperty("message").GetString()!,
                StringComparison.OrdinalIgnoreCase);

            int secondContinueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument secondContinued = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(secondContinued.RootElement, "continued");
            using JsonDocument secondContinue = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                secondContinue.RootElement,
                secondContinueSequence,
                "continue",
                success: true);

            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument exited = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(exited.RootElement, "exited");
            using JsonDocument terminated = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(terminated.RootElement, "terminated");
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private static async Task<JsonElement> ReadEvaluationAsync(
        DapTestClient client,
        int frameId,
        string expression,
        bool success,
        CancellationToken cancellationToken)
    {
        int sequence = await client.SendRequestAsync(
            "evaluate",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("expression", expression);
                writer.WriteNumber("frameId", frameId);
                writer.WriteString("context", "watch");
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "evaluate", success);
        return (success
            ? response.RootElement.GetProperty("body")
            : response.RootElement).Clone();
    }

    private static async Task<JsonElement> ReadSetVariableAsync(
        DapTestClient client,
        int variablesReference,
        string name,
        string value,
        bool success,
        CancellationToken cancellationToken)
    {
        int sequence = await client.SendRequestAsync(
            "setVariable",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("variablesReference", variablesReference);
                writer.WriteString("name", name);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "setVariable", success);
        JsonElement result = (success
            ? response.RootElement.GetProperty("body")
            : response.RootElement).Clone();
        if (success)
        {
            await AssertAssignmentInvalidationAsync(
                client,
                targetCodeExecuted: false,
                cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static async Task<JsonElement[]> ReadCompletionsAsync(
        DapTestClient client,
        int frameId,
        string text,
        CancellationToken cancellationToken)
    {
        int sequence = await client.SendRequestAsync(
            "completions",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("frameId", frameId);
                writer.WriteString("text", text);
                writer.WriteNumber("column", checked(text.Length + 1));
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "completions", success: true);
        return [.. response.RootElement
            .GetProperty("body")
            .GetProperty("targets")
            .EnumerateArray()
            .Select(static completion => completion.Clone())];
    }

    private static async Task<JsonElement> ReadSetExpressionAsync(
        DapTestClient client,
        int frameId,
        string expression,
        string value,
        bool success,
        CancellationToken cancellationToken,
        bool targetCodeExecuted = false)
    {
        int sequence = await client.SendRequestAsync(
            "setExpression",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("frameId", frameId);
                writer.WriteString("expression", expression);
                writer.WriteString("value", value);
                writer.WriteEndObject();
            },
            cancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "setExpression", success);
        JsonElement result = (success
            ? response.RootElement.GetProperty("body")
            : response.RootElement).Clone();
        if (success || targetCodeExecuted)
        {
            await AssertAssignmentInvalidationAsync(
                client,
                targetCodeExecuted,
                cancellationToken)
                .ConfigureAwait(false);
        }

        return result;
    }

    private static async Task AssertAssignmentInvalidationAsync(
        DapTestClient client,
        bool targetCodeExecuted,
        CancellationToken cancellationToken)
    {
        using JsonDocument invalidated = await client.ReadMessageAsync(cancellationToken)
            .ConfigureAwait(false);
        AssertEvent(invalidated.RootElement, "invalidated");
        string[] areas = [.. invalidated.RootElement
            .GetProperty("body")
            .GetProperty("areas")
            .EnumerateArray()
            .Select(static area => area.GetString()!)];
        Assert.AreSequenceEqual(
            targetCodeExecuted
                ? s_targetCodeInvalidationAreas
                : s_variableInvalidationAreas,
            areas);
    }

}

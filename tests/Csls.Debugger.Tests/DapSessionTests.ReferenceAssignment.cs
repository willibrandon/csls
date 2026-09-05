using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies assignment safety for identically named runtime types from separate load contexts.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rejects direct null writes into a managed interior pointer while keeping its referent writable.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceAssignmentRejectsNullIntoManagedByReference()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartPresentationFixtureAsync(
                waitPath, "ReferenceAssignmentFixture.cs", "int result = DebuggerFixture.WaitForSignal(",
                "--debugger-reference-assignment-fixture").ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int frameId = await GetReferenceAssignmentFrameAsync(client).ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "target", "\"reference-assignment-value\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "alias", "\"reference-assignment-value\"").ConfigureAwait(false);

            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, "alias", "null", success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains("managed by-reference", message, StringComparison.Ordinal);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "target", "\"reference-assignment-value\"").ConfigureAwait(false);
            await AssertStringIdentityExpressionAsync(
                client, frameId, "alias", "\"reference-assignment-value\"").ConfigureAwait(false);

            JsonElement cleared = await ReadSetExpressionAsync(
                client, frameId, "target", "null", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("null", cleared.GetProperty("value").GetString());
            Assert.AreEqual(0, cleared.GetProperty("variablesReference").GetInt32());
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
    /// Replaces a live generic receiver and a typed null with another instance of the same exact runtime type.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceAssignmentPreservesExactConstructedType()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath, isolateResultsViewAssembly: true)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement originalArray = await ReadEvaluationAsync(
                client, frameId, "localResultsView._items", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            int originalReference = originalArray.GetProperty("variablesReference").GetInt32();
            JsonElement[] originalItems = await ReadVariablesAsync(client, originalReference).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["71", "72", "73"], originalItems.Select(item => item.GetProperty("value").GetString()).ToArray());

            JsonElement replacement = await ReadSetExpressionAsync(
                client, frameId, "localResultsView", "localResultsViewEmpty", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.ResultsViewFixture<int>",
                replacement.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, replacement.GetProperty("variablesReference").GetInt32());
            await AssertReferenceAssignmentEmptyArrayAsync(client, frameId).ConfigureAwait(false);
            JsonElement[] retainedItems = await ReadVariablesAsync(client, originalReference).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["71", "72", "73"], retainedItems.Select(item => item.GetProperty("value").GetString()).ToArray());

            JsonElement cleared = await ReadSetExpressionAsync(
                client, frameId, "localResultsView", "null", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("null", cleared.GetProperty("value").GetString());
            Assert.AreEqual(0, cleared.GetProperty("variablesReference").GetInt32());
            JsonElement restored = await ReadSetExpressionAsync(
                client, frameId, "localResultsView", "localResultsViewEmpty", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Csls.TestProcessHost.ResultsViewFixture<int>", restored.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, restored.GetProperty("variablesReference").GetInt32());
            await AssertReferenceAssignmentEmptyArrayAsync(client, frameId).ConfigureAwait(false);
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
    /// Rejects a foreign runtime reference before changing a typed array element or executing target code.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReferenceAssignmentRejectsDifferentAssemblyLoadContext()
    {
        const string Target = "localResultsViewDefaultContext._items[0]";
        const string Source = "localResultsViewIsolatedContext._items[0]";
        const string ElementType = "Csls.TestProcessHost.ResultsViewElement";
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath, isolateResultsViewAssembly: true)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement destination = await ReadEvaluationAsync(
                client, frameId, Target, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement source = await ReadEvaluationAsync(
                client, frameId, Source, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(ElementType, destination.GetProperty("type").GetString());
            Assert.AreEqual(ElementType, source.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, destination.GetProperty("variablesReference").GetInt32());
            Assert.IsGreaterThan(0, source.GetProperty("variablesReference").GetInt32());
            await AssertReferenceElementFieldAsync(client, frameId, Target).ConfigureAwait(false);
            await AssertReferenceElementFieldAsync(client, frameId, Source).ConfigureAwait(false);

            JsonElement sameInstance = await ReadSetExpressionAsync(
                client, frameId, Target, Target, success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(ElementType, sameInstance.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, sameInstance.GetProperty("variablesReference").GetInt32());
            await AssertReferenceElementFieldAsync(client, frameId, Target).ConfigureAwait(false);

            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, Target, Source, success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string? message = rejected.GetProperty("message").GetString();
            Assert.IsNotNull(message);
            Assert.Contains(ElementType, message, StringComparison.Ordinal);
            Assert.Contains("No implicit reference conversion exists", message, StringComparison.Ordinal);
            await AssertReferenceElementFieldAsync(client, frameId, Target).ConfigureAwait(false);
            await AssertReferenceElementFieldAsync(client, frameId, Source).ConfigureAwait(false);

            JsonElement cleared = await ReadSetExpressionAsync(
                client, frameId, Target, "null", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("null", cleared.GetProperty("value").GetString());
            Assert.AreEqual(0, cleared.GetProperty("variablesReference").GetInt32());
            JsonElement afterClear = await ReadEvaluationAsync(
                client, frameId, Target, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("null", afterClear.GetProperty("result").GetString());
            Assert.AreEqual(ElementType, afterClear.GetProperty("type").GetString());
            Assert.AreEqual(0, afterClear.GetProperty("variablesReference").GetInt32());

            JsonElement rejectedIntoNull = await ReadSetExpressionAsync(
                client, frameId, Target, Source, success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(message, rejectedIntoNull.GetProperty("message").GetString());
            JsonElement afterNullRejection = await ReadEvaluationAsync(
                client, frameId, Target, success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("null", afterNullRejection.GetProperty("result").GetString());
            Assert.AreEqual(ElementType, afterNullRejection.GetProperty("type").GetString());
            Assert.AreEqual(0, afterNullRejection.GetProperty("variablesReference").GetInt32());
            await AssertReferenceElementFieldAsync(client, frameId, Source).ConfigureAwait(false);
            JsonElement unchangedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            Assert.AreEqual(frameId, unchangedFrame.GetProperty("id").GetInt32());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task AssertReferenceElementFieldAsync(DapTestClient client, int frameId, string expression)
    {
        JsonElement field = await ReadEvaluationAsync(
            client, frameId, expression + "._value", success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("131", field.GetProperty("result").GetString());
        Assert.AreEqual("int", field.GetProperty("type").GetString());
        Assert.AreEqual(0, field.GetProperty("variablesReference").GetInt32());
    }

    private async Task AssertReferenceAssignmentEmptyArrayAsync(DapTestClient client, int frameId)
    {
        JsonElement emptyArray = await ReadEvaluationAsync(
            client, frameId, "localResultsView._items", success: true, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual("int[]", emptyArray.GetProperty("type").GetString());
        Assert.AreEqual("{int[0]}", emptyArray.GetProperty("result").GetString());
        Assert.IsEmpty(await ReadVariablesAsync(client, emptyArray.GetProperty("variablesReference").GetInt32())
            .ConfigureAwait(false));
    }

    private async Task<int> GetReferenceAssignmentFrameAsync(DapTestClient client)
    {
        int threadsSequence = await client.SendRequestAsync(
            "threads", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
        foreach (int threadId in threads.RootElement.GetProperty("body").GetProperty("threads")
            .EnumerateArray().Select(static thread => thread.GetProperty("id").GetInt32()))
        {
            int sequence = await client.SendRequestAsync(
                "stackTrace", writer => WriteStackArguments(writer, threadId), TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument stack = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(stack.RootElement, sequence, "stackTrace", success: true);
            foreach (JsonElement frame in stack.RootElement.GetProperty("body").GetProperty("stackFrames")
                .EnumerateArray().Where(static frame =>
                    frame.TryGetProperty("source", out JsonElement source) &&
                    source.GetProperty("path").GetString() is string path &&
                    path.EndsWith("ReferenceAssignmentFixture.cs", StringComparison.Ordinal)))
            {
                return frame.GetProperty("id").GetInt32();
            }
        }

        Assert.Fail("No managed frame resolved to the reference-assignment fixture.");
        return 0;
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies logical frame continuity across invisible evaluation and retirement on explicit execution.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Refreshes scopes through the original frame before requesting a replacement stack after enumeration.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewRefreshesOriginalFrameScopesBeforeStackRefresh()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (int oldLocalsReference, _) = await ReadLogicalFrameLocalsAsync(client, frameId)
                .ConfigureAwait(false);
            JsonElement lazy = await ReadResultsViewRowAsync(client, "localResultsView")
                .ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = snapshot.GetProperty("variablesReference").GetInt32();

            (int localsReference, JsonElement[] locals) = await ReadLogicalFrameLocalsAsync(client, frameId)
                .ConfigureAwait(false);
            Assert.AreNotEqual(oldLocalsReference, localsReference);
            Assert.AreEqual("43", Assert.ContainsSingle(locals.Where(local =>
                local.GetProperty("name").GetString() == "localNumber")).GetProperty("value").GetString());
            JsonElement receiver = Assert.ContainsSingle(locals.Where(local =>
                local.GetProperty("name").GetString() == "localResultsView"));
            JsonElement[] fields = await ReadVariablesAsync(
                client, receiver.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            Assert.AreEqual("1", Assert.ContainsSingle(fields.Where(field =>
                field.GetProperty("name").GetString() == "_enumerationCount")).GetProperty("value").GetString());
            JsonElement reused = Assert.ContainsSingle(fields.Where(field =>
                field.GetProperty("name").GetString() == "Results View"));
            Assert.AreEqual(snapshotReference, reused.GetProperty("variablesReference").GetInt32());
            Assert.AreEqual(3, reused.GetProperty("indexedVariables").GetInt32());
            Assert.AreEqual(0, reused.GetProperty("namedVariables").GetInt32());
            JsonElement hint = reused.GetProperty("presentationHint");
            Assert.IsFalse(hint.TryGetProperty("lazy", out JsonElement isLazy) && isLazy.GetBoolean());
            JsonElement[] items = await ReadVariablesAsync(client, snapshotReference).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["[0]", "[1]", "[2]"],
                items.Select(item => item.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(
                ["71", "72", "73"],
                items.Select(item => item.GetProperty("value").GetString()).ToArray());

            int staleSequence = await client.SendRequestAsync(
                "variables", writer => WriteResultsViewReference(writer, oldLocalsReference),
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument stale = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(stale.RootElement, staleSequence, "variables", success: false);
                string? message = stale.RootElement.GetProperty("message").GetString();
                Assert.IsNotNull(message);
                Assert.Contains("stale", message, StringComparison.OrdinalIgnoreCase);
            }

            JsonElement refreshed = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            AssertSameLogicalFrame(frame, refreshed);
            await AssertEnumerationCountAsync(client, "localResultsView", 1).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Keeps the original source frame addressable after guarded proxy construction and property evaluation.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerTypeProxyRefreshesOriginalFrameScopesBeforeStackRefresh()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            JsonElement[] proxy = await ReadProxyLocalAsync(client, "localGenericProxy")
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["Value", "Raw View"],
                proxy.Select(field => field.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("49", proxy[0].GetProperty("value").GetString());

            (_, JsonElement[] locals) = await ReadLogicalFrameLocalsAsync(client, frameId)
                .ConfigureAwait(false);
            Assert.AreEqual("43", Assert.ContainsSingle(locals.Where(local =>
                local.GetProperty("name").GetString() == "localNumber")).GetProperty("value").GetString());
            JsonElement evaluation = await ReadEvaluationAsync(
                client, frameId, "localNumber", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("43", evaluation.GetProperty("result").GetString());
            JsonElement refreshed = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            AssertSameLogicalFrame(frame, refreshed);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    /// <summary>
    /// Rejects a previous logical frame after explicit execution even when the same method is stopped again.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ExplicitContinueRetiresLogicalFrameForScopesAndEvaluation()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            await ContinueAndPauseAsync(client, expectedOutput: "ready").ConfigureAwait(false);
            int scopesSequence = await client.SendRequestAsync(
                "scopes", writer => WriteFrameArguments(writer, frameId), TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: false);
                string? message = scopes.RootElement.GetProperty("message").GetString();
                Assert.IsNotNull(message);
                Assert.Contains("stale", message, StringComparison.OrdinalIgnoreCase);
            }

            JsonElement evaluation = await ReadEvaluationAsync(
                client, frameId, "localNumber", success: false, TestContext.CancellationToken)
                .ConfigureAwait(false);
            string? error = evaluation.GetProperty("message").GetString();
            Assert.IsNotNull(error);
            Assert.Contains("stale", error, StringComparison.OrdinalIgnoreCase);
            JsonElement currentFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int currentFrameId = currentFrame.GetProperty("id").GetInt32();
            Assert.AreNotEqual(frameId, currentFrameId);
            (_, JsonElement[] locals) = await ReadLogicalFrameLocalsAsync(client, currentFrameId)
                .ConfigureAwait(false);
            Assert.AreEqual("43", Assert.ContainsSingle(locals.Where(local =>
                local.GetProperty("name").GetString() == "localNumber")).GetProperty("value").GetString());
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<(int Reference, JsonElement[] Variables)> ReadLogicalFrameLocalsAsync(
        DapTestClient client,
        int frameId)
    {
        int sequence = await client.SendRequestAsync(
            "scopes", writer => WriteFrameArguments(writer, frameId), TestContext.CancellationToken)
            .ConfigureAwait(false);
        using JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(scopes.RootElement, sequence, "scopes", success: true);
        JsonElement localScope = Assert.ContainsSingle(scopes.RootElement.GetProperty("body")
            .GetProperty("scopes").EnumerateArray().Where(scope =>
                scope.GetProperty("name").GetString() == "Locals"));
        int reference = localScope.GetProperty("variablesReference").GetInt32();
        return (reference, await ReadVariablesAsync(client, reference).ConfigureAwait(false));
    }

    private static void AssertSameLogicalFrame(JsonElement expected, JsonElement actual)
    {
        Assert.AreEqual(expected.GetProperty("id").GetInt32(), actual.GetProperty("id").GetInt32());
        Assert.AreEqual(expected.GetProperty("name").GetString(), actual.GetProperty("name").GetString());
        Assert.AreEqual(expected.GetProperty("line").GetInt32(), actual.GetProperty("line").GetInt32());
        Assert.AreEqual(expected.GetProperty("column").GetInt32(), actual.GetProperty("column").GetInt32());
        Assert.AreEqual(expected.GetProperty("source").GetProperty("path").GetString(),
            actual.GetProperty("source").GetProperty("path").GetString());
    }
}

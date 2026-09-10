using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Preserves authorized inspection when whole-value assignment is rejected before mutation.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Keeps a materialized enumerable and its pages usable after rejecting an incompatible struct.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StructAssignmentRejectionPreservesResultsViewSnapshot()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement lazy = await ReadResultsViewRowAsync(client, "localResultsViewStruct")
                .ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int snapshotReference = await AssertAssignedStructSnapshotAsync(client, snapshot)
                .ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();

            JsonElement rejected = await ReadSetExpressionAsync(
                client, frameId, "localResultsViewStruct", "localTuple", success: false,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual("Whole-value assignment requires identical loaded runtime types.",
                rejected.GetProperty("message").GetString());
            Assert.AreEqual(snapshotReference,
                await AssertAssignedStructSnapshotAsync(client, snapshot).ConfigureAwait(false));
            await AssertAssignedStructSnapshotReuseAsync(
                client, frameId, "localResultsViewStruct", snapshotReference).ConfigureAwait(false);
            await AssertAssignedStructStorageAsync(client, frameId, "localResultsViewStruct", 151, 152, 1)
                .ConfigureAwait(false);
            JsonElement source = await ReadEvaluationAsync(
                client, frameId, "localTuple", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("(42, \"answer\")", source.GetProperty("result").GetString());
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

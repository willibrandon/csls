using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded variable pages over real retained runtime and synthetic snapshots.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Clamps valid large ranges to the retained child set without re-enumerating the target.
    /// </summary>
    /// <param name="localName">The fixture enumerable that produces the retained snapshot.</param>
    /// <param name="filter">The semantic category containing the snapshot children.</param>
    /// <param name="names">The exact complete child-name sequence.</param>
    /// <param name="values">The exact complete formatted child-value sequence.</param>
    [TestMethod]
    [DataRow("localResultsView", "indexed",
        new[] { "[0]", "[1]", "[2]" }, new[] { "71", "72", "73" })]
    [DataRow("localResultsViewEmpty", "named",
        new[] { "Empty" }, new[] { "\"Enumeration yielded no results\"" })]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewSnapshotClampsLargePageRanges(
        string localName,
        string filter,
        string[] names,
        string[] values)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(values);
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement row = await ReadResultsViewRowAsync(client, localName).ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, row.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int reference = snapshot.GetProperty("variablesReference").GetInt32();
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            (int Start, int Count)[] ranges =
            [
                (0, int.MaxValue),
                (1, int.MaxValue),
                (names.Length, 0),
                (names.Length, int.MaxValue),
                (int.MaxValue, 1),
                (int.MaxValue, int.MaxValue)
            ];
            foreach ((int start, int count) in ranges)
            {
                JsonElement[] page = await ReadResultsViewSnapshotPageAsync(
                    client, reference, start, count, filter).ConfigureAwait(false);
                Assert.AreSequenceEqual(
                    names.Skip(start).ToArray(),
                    page.Select(variable => variable.GetProperty("name").GetString()).ToArray());
                Assert.AreSequenceEqual(
                    values.Skip(start).ToArray(),
                    page.Select(variable => variable.GetProperty("value").GetString()).ToArray());
                JsonElement counter = await ReadEvaluationAsync(
                    client, frameId, $"{localName}._enumerationCount", success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual("1", counter.GetProperty("result").GetString());
            }

            string excludedFilter = filter == "indexed" ? "named" : "indexed";
            Assert.IsEmpty(await ReadResultsViewSnapshotPageAsync(
                client, reference, 0, int.MaxValue, excludedFilter).ConfigureAwait(false));
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

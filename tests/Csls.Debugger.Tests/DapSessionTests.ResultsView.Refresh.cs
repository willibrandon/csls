using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies retained enumeration after editors refresh the stopped stack and variable tree.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reuses one authorized snapshot when fresh scopes rediscover the same stopped enumerable.
    /// </summary>
    /// <param name="localName">The original reference, value, nullable, or boxed enumerable.</param>
    /// <param name="structCounter">Whether enumeration state belongs to the struct's shared counter.</param>
    /// <param name="expectedValues">The exact values in the authorized snapshot.</param>
    [TestMethod]
    [DataRow("localResultsView", false, new[] { "71", "72", "73" })]
    [DataRow("localResultsViewEmpty", false, new[] { "\"Enumeration yielded no results\"" })]
    [DataRow("localResultsViewStruct", true, new[] { "151", "152" })]
    [DataRow("localResultsViewNullableStruct", true, new[] { "151", "152" })]
    [DataRow("localResultsViewBoxedStruct", true, new[] { "151", "152" })]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ResultsViewSnapshotSurvivesRefreshedScopes(
        string localName,
        bool structCounter,
        string[] expectedValues)
    {
        ArgumentNullException.ThrowIfNull(expectedValues);
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement lazy = await ReadResultsViewRowAsync(client, localName).ConfigureAwait(false);
            JsonElement snapshot = await ResolveResultsViewSnapshotAsync(
                client, lazy.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false);
            int reference = snapshot.GetProperty("variablesReference").GetInt32();
            bool empty = localName == "localResultsViewEmpty";
            Assert.AreEqual(empty ? 1 : 0, snapshot.GetProperty("namedVariables").GetInt32());
            Assert.AreEqual(
                empty ? 0 : expectedValues.Length,
                snapshot.GetProperty("indexedVariables").GetInt32());
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = frame.GetProperty("id").GetInt32();
            for (int refresh = 0; refresh < 2; refresh++)
            {
                JsonElement[] fields = await ReadUnproxiedLocalAsync(client, localName)
                    .ConfigureAwait(false);
                JsonElement rediscovered = Assert.ContainsSingle(fields.Where(field =>
                    field.GetProperty("name").GetString() == "Results View"));
                JsonElement hint = rediscovered.GetProperty("presentationHint");
                Assert.IsFalse(
                    hint.TryGetProperty("lazy", out JsonElement lazyHint) && lazyHint.GetBoolean(),
                    "Refreshing the same stopped enumerable must retain its authorized snapshot.");
                Assert.AreEqual(reference, rediscovered.GetProperty("variablesReference").GetInt32());
                Assert.AreEqual(
                    snapshot.GetProperty("indexedVariables").GetInt32(),
                    rediscovered.GetProperty("indexedVariables").GetInt32());
                Assert.AreEqual(
                    snapshot.GetProperty("namedVariables").GetInt32(),
                    rediscovered.GetProperty("namedVariables").GetInt32());
                Assert.AreSequenceEqual(
                    ["readOnly"],
                    hint.GetProperty("attributes").EnumerateArray()
                        .Select(attribute => attribute.GetString()).ToArray());
                JsonElement[] items = await ReadVariablesAsync(client, reference).ConfigureAwait(false);
                Assert.AreSequenceEqual(
                    expectedValues,
                    items.Select(item => item.GetProperty("value").GetString()).ToArray());
                if (structCounter)
                {
                    await AssertStructEnumerationCountAsync(client, localName, 1).ConfigureAwait(false);
                }
                else
                {
                    await AssertEnumerationCountAsync(client, localName, 1).ConfigureAwait(false);
                }

                JsonElement refreshedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
                Assert.AreEqual(frameId, refreshedFrame.GetProperty("id").GetInt32());
            }

            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

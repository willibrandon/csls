using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies debugger presentation metadata over real stopped runtime values.
/// </summary>
public sealed partial class DapSessionTests
{
    private static readonly string[] s_defaultDebuggerBrowsableNames =
    [
        "_visible",
        "_collapsed",
        "[0]",
        "[1]",
        "_nested",
        "_scalarRoot",
        "_self",
        "_missing",
        "Raw View"
    ];
    private static readonly string[] s_rawDebuggerBrowsableNames =
    [
        "_visible",
        "_hidden",
        "_collapsed",
        "_rootItems",
        "_rootObject",
        "_scalarRoot",
        "_self",
        "_missing"
    ];
    private static readonly string[] s_pagedDebuggerBrowsableNames = ["_collapsed", "[0]"];

    /// <summary>
    /// Honors hidden, collapsed, and root-hidden fields while preserving a raw view.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DebuggerBrowsableMetadataShapesDefaultAndRawViews()
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-browsable-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await StartStoppedFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int scopesSequence = await client.SendRequestAsync(
                "scopes",
                writer => WriteFrameArguments(writer, frame.GetProperty("id").GetInt32()),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument scopes = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
            JsonElement locals = scopes.RootElement.GetProperty("body").GetProperty("scopes")
                .EnumerateArray().Single(scope =>
                    scope.GetProperty("name").GetString() == "Locals");
            JsonElement localBrowsable = (await ReadVariablesAsync(
                client,
                locals.GetProperty("variablesReference").GetInt32()).ConfigureAwait(false))
                .Single(variable =>
                    variable.GetProperty("name").GetString() == "localBrowsable");

            int defaultReference = localBrowsable.GetProperty("variablesReference").GetInt32();
            JsonElement[] defaultView = await ReadVariablesAsync(client, defaultReference)
                .ConfigureAwait(false);
            Assert.AreSequenceEqual(
                s_defaultDebuggerBrowsableNames,
                defaultView.Select(variable => variable.GetProperty("name").GetString()).ToArray());
            Assert.AreEqual("49", defaultView[2].GetProperty("value").GetString());
            Assert.AreEqual(
                "localBrowsable._rootItems[0]",
                defaultView[2].GetProperty("evaluateName").GetString());
            Assert.AreEqual("50", defaultView[3].GetProperty("value").GetString());
            Assert.AreEqual("51", defaultView[4].GetProperty("value").GetString());
            Assert.AreEqual(
                "localBrowsable._rootObject._nested",
                defaultView[4].GetProperty("evaluateName").GetString());
            Assert.AreEqual("52", defaultView[5].GetProperty("value").GetString());
            Assert.AreEqual("_self", defaultView[6].GetProperty("name").GetString());
            Assert.AreEqual("null", defaultView[7].GetProperty("value").GetString());

            JsonElement rawView = defaultView[8];
            Assert.AreEqual(
                "virtual",
                rawView.GetProperty("presentationHint").GetProperty("kind").GetString());
            int rawReference = rawView.GetProperty("variablesReference").GetInt32();
            Assert.IsGreaterThan(0, rawReference);
            JsonElement[] rawFields = await ReadVariablesAsync(client, rawReference)
                .ConfigureAwait(false);
            string?[] rawFieldNames =
            [
                .. rawFields.Select(variable => variable.GetProperty("name").GetString())
            ];
            Assert.AreSequenceEqual(
                s_rawDebuggerBrowsableNames,
                rawFieldNames);
            Assert.AreEqual("47", rawFields[1].GetProperty("value").GetString());
            Assert.AreEqual(
                "localBrowsable._hidden",
                rawFields[1].GetProperty("evaluateName").GetString());
            Assert.DoesNotContain(
                "Raw View",
                rawFieldNames);

            JsonElement[] page = await ReadVariablesPageAsync(
                client,
                defaultReference,
                start: 1,
                count: 2).ConfigureAwait(false);
            Assert.AreSequenceEqual(
                s_pagedDebuggerBrowsableNames,
                page.Select(variable => variable.GetProperty("name").GetString()).ToArray());

            JsonElement[] indexed = await ReadVariablesPageAsync(
                client, defaultReference, start: 0, count: 0, filter: "indexed").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["[0]", "[1]"],
                indexed.Select(variable => variable.GetProperty("name").GetString()).ToArray());
            Assert.AreSequenceEqual(
                ["49", "50"],
                indexed.Select(variable => variable.GetProperty("value").GetString()).ToArray());
            JsonElement indexedPage = Assert.ContainsSingle(await ReadVariablesPageAsync(
                client, defaultReference, start: 1, count: 1, filter: "indexed").ConfigureAwait(false));
            Assert.AreEqual("[1]", indexedPage.GetProperty("name").GetString());
            Assert.AreEqual("50", indexedPage.GetProperty("value").GetString());
            JsonElement[] named = await ReadVariablesPageAsync(
                client, defaultReference, start: 0, count: 0, filter: "named").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["_visible", "_collapsed", "_nested", "_scalarRoot", "_self", "_missing", "Raw View"],
                named.Select(variable => variable.GetProperty("name").GetString()).ToArray());
            JsonElement[] namedPage = await ReadVariablesPageAsync(
                client, defaultReference, start: 1, count: 2, filter: "named").ConfigureAwait(false);
            Assert.AreSequenceEqual(
                ["_collapsed", "_nested"],
                namedPage.Select(variable => variable.GetProperty("name").GetString()).ToArray());
            Assert.IsEmpty(await ReadVariablesPageAsync(
                client, rawReference, start: 0, count: 0, filter: "indexed").ConfigureAwait(false));

            await ResumeAndReleaseFixtureAsync(client, waitPath).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private async Task<JsonElement[]> ReadVariablesPageAsync(
        DapTestClient client,
        int variablesReference,
        int start,
        int count,
        string? filter = null)
    {
        int sequence = await client.SendRequestAsync(
            "variables",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("variablesReference", variablesReference);
                writer.WriteNumber("start", start);
                writer.WriteNumber("count", count);
                if (filter is not null)
                {
                    writer.WriteString("filter", filter);
                }

                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "variables", success: true);
        return
        [
            .. response.RootElement.GetProperty("body").GetProperty("variables")
                .EnumerateArray().Select(variable => variable.Clone())
        ];
    }
}

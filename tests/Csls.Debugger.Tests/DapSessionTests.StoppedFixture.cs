using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Locates the source frame in a real paused debugger fixture.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<JsonElement> GetFixtureFrameAsync(DapTestClient client)
    {
        int threadsSequence = await client.SendRequestAsync(
            "threads",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, threadsSequence, "threads", success: true);
        foreach (JsonElement thread in threads.RootElement.GetProperty("body")
            .GetProperty("threads").EnumerateArray())
        {
            JsonElement? frame = await FindFixtureFrameAsync(
                client,
                thread.GetProperty("id").GetInt32()).ConfigureAwait(false);
            if (frame is not null)
            {
                return frame.Value;
            }
        }

        Assert.Fail("No managed stack frame resolved to the debugger fixture.");
        return default;
    }

    private async Task<JsonElement?> FindFixtureFrameAsync(
        DapTestClient client,
        int threadId)
    {
        int sequence = await client.SendRequestAsync(
            "stackTrace",
            writer => WriteStackArguments(writer, threadId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stackTrace", success: true);
        JsonElement frame = response.RootElement.GetProperty("body").GetProperty("stackFrames")
            .EnumerateArray().FirstOrDefault(candidate =>
                candidate.TryGetProperty("source", out JsonElement source) &&
                source.GetProperty("path").GetString() is string path &&
                path.EndsWith("DebuggerFixture.cs", StringComparison.Ordinal));
        return frame.ValueKind == JsonValueKind.Undefined ? null : frame.Clone();
    }
}

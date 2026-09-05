using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP retrieval and stack identity for embedded source documents.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task AssertEmbeddedSourceContentAsync(
        DapTestClient client,
        int sourceReference)
    {
        int sequence = await client.SendRequestAsync(
            "source",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("sourceReference", sourceReference);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "source", success: true);
        JsonElement body = response.RootElement.GetProperty("body");
        Assert.AreEqual("text/x-csharp", body.GetProperty("mimeType").GetString());
        Assert.Contains(
            "int embeddedNumber = number + 1;",
            body.GetProperty("content").GetString()!,
            StringComparison.Ordinal);
    }

    private async Task AssertEmbeddedStackSourceAsync(
        DapTestClient client,
        int threadId,
        int sourceReference)
    {
        int sequence = await client.SendRequestAsync(
            "stackTrace",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stackTrace", success: true);
        JsonElement source = response.RootElement
            .GetProperty("body")
            .GetProperty("stackFrames")
            .EnumerateArray()
            .First(static frame => frame.GetProperty("line").GetInt32() > 0)
            .GetProperty("source");
        Assert.AreEqual(sourceReference, source.GetProperty("sourceReference").GetInt32());
        Assert.AreEqual("embedded source", source.GetProperty("origin").GetString());
    }
}

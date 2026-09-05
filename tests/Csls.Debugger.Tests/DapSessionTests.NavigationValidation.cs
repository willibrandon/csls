using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies generation validation for source-aware DAP execution navigation.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task AssertGotoTargetIsStaleAsync(
        DapTestClient client,
        int threadId,
        int targetId)
    {
        int sequence = await client.SendRequestAsync(
            "goto",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteNumber("targetId", targetId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "goto", success: false);
        Assert.Contains(
            "stale",
            response.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertStepTargetIsStaleAsync(
        DapTestClient client,
        int threadId,
        int targetId)
    {
        int sequence = await client.SendRequestAsync(
            "stepIn",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteNumber("threadId", threadId);
                writer.WriteNumber("targetId", targetId);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stepIn", success: false);
        Assert.Contains(
            "stale",
            response.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }
}

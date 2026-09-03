using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Provides compact DAP protocol helpers for memory integration tests.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task AssertOversizedMemoryReadRejectedAsync(
        DapTestClient client,
        string memoryReference)
    {
        int sequence = await client.SendRequestAsync(
            "readMemory",
            writer => WriteMemoryArguments(writer, memoryReference, 0, (1024 * 1024) + 1),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "readMemory", success: false);
        Assert.Contains(
            "cannot exceed",
            response.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task PauseFixtureAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "pause",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument stopped = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(stopped.RootElement, "stopped");
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "pause", success: true);
    }

    private async Task ContinueAndPauseAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument continued = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(continued.RootElement, "continued");
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "continue", success: true);
        await PauseFixtureAsync(client).ConfigureAwait(false);
    }

    private async Task ResumeAndReleaseFixtureAsync(DapTestClient client, string waitPath)
    {
        int sequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument continued = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(continued.RootElement, "continued");
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "continue", success: true);
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
    }

    private static void WriteMemoryArguments(
        Utf8JsonWriter writer,
        string memoryReference,
        long offset,
        int count)
    {
        writer.WriteStartObject();
        writer.WriteString("memoryReference", memoryReference);
        writer.WriteNumber("offset", offset);
        writer.WriteNumber("count", count);
        writer.WriteEndObject();
    }

    private static void WriteStackArguments(Utf8JsonWriter writer, int threadId)
    {
        writer.WriteStartObject();
        writer.WriteNumber("threadId", threadId);
        writer.WriteNumber("levels", 64);
        writer.WriteEndObject();
    }

    private static void WriteFrameArguments(Utf8JsonWriter writer, int frameId)
    {
        writer.WriteStartObject();
        writer.WriteNumber("frameId", frameId);
        writer.WriteEndObject();
    }
}

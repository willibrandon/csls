using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Drives managed-IL breakpoint requests through the real DAP transport.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<JsonElement> SetInstructionBreakpointAsync(
        DapTestClient client,
        string instructionReference,
        long offset)
    {
        int sequence = await client.SendRequestAsync(
            "setInstructionBreakpoints",
            writer => WriteInstructionBreakpoints(writer, instructionReference, offset),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(
            response.RootElement,
            sequence,
            "setInstructionBreakpoints",
            success: true);
        return response.RootElement.GetProperty("body").GetProperty("breakpoints")[0]
            .Clone();
    }

    private async Task<int> ContinueToInstructionBreakpointAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        bool response = false;
        bool continued = false;
        int? threadId = null;
        while (!response || !continued || threadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, sequence, "continue", success: true);
                response = true;
            }
            else if (root.GetProperty("event").GetString() == "continued")
            {
                continued = true;
            }
            else if (root.GetProperty("event").GetString() == "stopped")
            {
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual(
                    "instruction breakpoint",
                    body.GetProperty("reason").GetString());
                threadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return threadId.Value;
    }

    private async Task ClearInstructionBreakpointsAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "setInstructionBreakpoints",
            WriteEmptyInstructionBreakpoints,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(
            response.RootElement,
            sequence,
            "setInstructionBreakpoints",
            success: true);
        Assert.HasCount(0, response.RootElement.GetProperty("body")
            .GetProperty("breakpoints").EnumerateArray().ToArray());
    }

    private static void WriteInstructionBreakpoints(
        Utf8JsonWriter writer,
        string instructionReference,
        long offset)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteString("instructionReference", instructionReference);
        writer.WriteNumber("offset", offset);
        writer.WriteString("hitCondition", "3");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEmptyInstructionBreakpoints(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

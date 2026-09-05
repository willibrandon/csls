using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Drives source-aware step and goto requests through the real DAP transport.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<JsonElement> ReadTopSourceFrameAsync(
        DapTestClient client,
        int threadId)
    {
        int stackSequence = await client.SendRequestAsync(
            "stackTrace",
            writer => WriteStackArguments(writer, threadId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument stack = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(stack.RootElement, stackSequence, "stackTrace", success: true);
        JsonElement frame = stack.RootElement.GetProperty("body").GetProperty("stackFrames")
            .EnumerateArray().First();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in frame.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteNumber("threadId", threadId);
            writer.WriteEndObject();
        }

        using var result = JsonDocument.Parse(buffer.ToArray());
        return result.RootElement.Clone();
    }

    private async Task<int> TargetedStepAndReadStopAsync(
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
        bool response = false;
        bool continued = false;
        int? stoppedThread = null;
        while (!response || !continued || stoppedThread is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, sequence, "stepIn", success: true);
                response = true;
            }
            else if (root.GetProperty("event").GetString() == "continued")
            {
                continued = true;
            }
            else if (root.GetProperty("event").GetString() == "stopped")
            {
                stoppedThread = root.GetProperty("body").GetProperty("threadId").GetInt32();
            }
        }

        return stoppedThread.Value;
    }

    private async Task<int> ReadGotoTargetAsync(
        DapTestClient client,
        string sourcePath,
        int line)
    {
        int sequence = await client.SendRequestAsync(
            "gotoTargets",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("source");
                writer.WriteString("path", sourcePath);
                writer.WriteEndObject();
                writer.WriteNumber("line", line);
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "gotoTargets", success: true);
        JsonElement[] targets = [.. response.RootElement.GetProperty("body")
            .GetProperty("targets").EnumerateArray()];
        Assert.HasCount(1, targets);
        Assert.AreEqual(line, targets[0].GetProperty("line").GetInt32());
        Assert.IsFalse(string.IsNullOrWhiteSpace(targets[0]
            .GetProperty("instructionPointerReference").GetString()));
        return targets[0].GetProperty("id").GetInt32();
    }

    private async Task GotoAndAssertOrderAsync(
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
        AssertResponse(response.RootElement, sequence, "goto", success: true);
        using JsonDocument stopped = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(stopped.RootElement, "stopped");
        Assert.AreEqual(
            "goto",
            stopped.RootElement.GetProperty("body").GetProperty("reason").GetString());
    }

    private async Task ClearSourceBreakpointsAsync(
        DapTestClient client,
        string sourcePath)
    {
        int sequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer =>
            {
                writer.WriteStartObject();
                writer.WriteStartObject("source");
                writer.WriteString("path", sourcePath);
                writer.WriteEndObject();
                writer.WriteStartArray("breakpoints");
                writer.WriteEndArray();
                writer.WriteEndObject();
            },
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "setBreakpoints", success: true);
        Assert.HasCount(0, response.RootElement.GetProperty("body")
            .GetProperty("breakpoints").EnumerateArray().ToArray());
    }

    private static void WriteFrameId(Utf8JsonWriter writer, int frameId)
    {
        writer.WriteStartObject();
        writer.WriteNumber("frameId", frameId);
        writer.WriteEndObject();
    }
}

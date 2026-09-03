using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed-IL disassembly over a real DAP session.
/// </summary>
public sealed partial class DapSessionTests
{
    private async Task<string> AssertManagedFrameDisassemblyAsync(DapTestClient client)
    {
        JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
        string reference = frame.GetProperty("instructionPointerReference").GetString()!;
        Assert.StartsWith("csls-il-", reference);

        JsonElement[] instructions = await ReadDisassemblyAsync(
            client,
            reference,
            offset: 0,
            instructionOffset: -4,
            instructionCount: 12).ConfigureAwait(false);
        Assert.HasCount(12, instructions);
        Assert.IsTrue(instructions.All(instruction =>
            instruction.GetProperty("address").GetString()!.StartsWith(
                "0x",
                StringComparison.Ordinal)));
        Assert.IsNotEmpty(instructions.Where(instruction =>
            instruction.TryGetProperty("instructionBytes", out _)).ToArray());
        Assert.IsNotEmpty(instructions.Where(instruction =>
            instruction.GetProperty("instruction").GetString()!.Contains(
                "System.Threading.Thread.Sleep",
                StringComparison.Ordinal)).ToArray());
        Assert.IsNotEmpty(instructions.Where(instruction =>
            instruction.TryGetProperty("location", out _) &&
            instruction.GetProperty("line").GetInt32() > 0).ToArray());

        JsonElement[] invalid = await ReadDisassemblyAsync(
            client,
            reference,
            offset: int.MaxValue,
            instructionOffset: 0,
            instructionCount: 3).ConfigureAwait(false);
        Assert.HasCount(3, invalid);
        Assert.IsTrue(invalid.All(instruction =>
            instruction.GetProperty("presentationHint").GetString() == "invalid"));
        return reference;
    }

    private async Task AssertStaleDisassemblyRejectedAsync(
        DapTestClient client,
        string reference)
    {
        int staleSequence = await client.SendRequestAsync(
            "disassemble",
            writer => WriteDisassemblyArguments(writer, reference, 0, 0, 1),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument stale = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(stale.RootElement, staleSequence, "disassemble", success: false);
        Assert.Contains(
            "stale",
            stale.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task<JsonElement[]> ReadDisassemblyAsync(
        DapTestClient client,
        string reference,
        long offset,
        long instructionOffset,
        int instructionCount)
    {
        int sequence = await client.SendRequestAsync(
            "disassemble",
            writer => WriteDisassemblyArguments(
                writer,
                reference,
                offset,
                instructionOffset,
                instructionCount),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "disassemble", success: true);
        return [.. response.RootElement.GetProperty("body").GetProperty("instructions")
            .EnumerateArray().Select(static instruction => instruction.Clone())];
    }

    private static void WriteDisassemblyArguments(
        Utf8JsonWriter writer,
        string reference,
        long offset,
        long instructionOffset,
        int instructionCount)
    {
        writer.WriteStartObject();
        writer.WriteString("memoryReference", reference);
        writer.WriteNumber("offset", offset);
        writer.WriteNumber("instructionOffset", instructionOffset);
        writer.WriteNumber("instructionCount", instructionCount);
        writer.WriteBoolean("resolveSymbols", true);
        writer.WriteEndObject();
    }
}

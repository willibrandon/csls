using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies inspection of optimized frame slots whose native storage lifetimes have ended.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves source names and page positions when expired locals and arguments precede live storage.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task UnavailableFrameSlotsPreserveFollowingValuesAndPages()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync("initialize",
            WriteVariablePagingInitializeArguments, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync("launch", writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("program", GetJitFixture());
            writer.WriteStartArray("args");
            writer.WriteStringValue("--unavailable-locals");
            writer.WriteEndArray();
            writer.WriteBoolean("suppressJITOptimizations", false);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int configurationSequence = await client.SendRequestAsync("configurationDone",
            WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument configuration = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(configuration.RootElement, configurationSequence, "configurationDone", success: true);
        using JsonDocument launch = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(launch.RootElement, launchSequence, "launch", success: true);
        using JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(process.RootElement, "process");
        string output = string.Empty;
        while (output.Length < "41ready".Length)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(message.RootElement, "output");
            output += message.RootElement.GetProperty("body").GetProperty("output").GetString();
        }

        Assert.AreEqual("41ready", output);
        await PauseFixtureAsync(client).ConfigureAwait(false);
        int frameId = await FindUnavailableLocalsFrameAsync(client).ConfigureAwait(false);
        (int reference, JsonElement[] locals) = await ReadLogicalFrameLocalsAsync(client, frameId)
            .ConfigureAwait(false);
        Assert.AreSequenceEqual(["expired", "retained"],
            locals.Select(static value => value.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual("42", locals[1].GetProperty("value").GetString());
        Assert.AreEqual("int", locals[1].GetProperty("type").GetString());
        AssertUnavailableFrameSlot(locals[0]);
        await AssertUnavailableScopePagesAsync(client, reference, locals).ConfigureAwait(false);

        int scopesSequence = await client.SendRequestAsync("scopes", writer => WriteFrameArguments(writer, frameId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument scopes = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(scopes.RootElement, scopesSequence, "scopes", success: true);
        JsonElement argumentsScope = Assert.ContainsSingle(scopes.RootElement.GetProperty("body").GetProperty("scopes")
            .EnumerateArray().Where(static scope => scope.GetProperty("name").GetString() == "Arguments"));
        int argumentsReference = argumentsScope.GetProperty("variablesReference").GetInt32();
        JsonElement[] arguments = await ReadVariablesAsync(client, argumentsReference).ConfigureAwait(false);
        Assert.AreSequenceEqual(["seed", "retainedArgument"],
            arguments.Select(static value => value.GetProperty("name").GetString()).ToArray());
        AssertUnavailableFrameSlot(arguments[0]);
        Assert.AreEqual("84", arguments[1].GetProperty("value").GetString());
        Assert.AreEqual("int", arguments[1].GetProperty("type").GetString());
        await AssertUnavailableScopePagesAsync(client, argumentsReference, arguments).ConfigureAwait(false);
        await DisconnectAsync(client).ConfigureAwait(false);
    }

    private static void AssertUnavailableFrameSlot(JsonElement variable)
    {
        Assert.AreEqual(0, variable.GetProperty("variablesReference").GetInt32());
        Assert.AreEqual("int", variable.GetProperty("type").GetString());
        Assert.IsFalse(variable.TryGetProperty("memoryReference", out _));
        Assert.IsFalse(variable.TryGetProperty("evaluateName", out _));
        Assert.AreSequenceEqual(["readOnly"], variable.GetProperty("presentationHint")
            .GetProperty("attributes").EnumerateArray().Select(static value => value.GetString()).ToArray());
        string? unavailable = variable.GetProperty("value").GetString();
        Assert.IsNotNull(unavailable);
        Assert.Contains("unavailable", unavailable, StringComparison.OrdinalIgnoreCase);
    }

    private async Task AssertUnavailableScopePagesAsync(DapTestClient client, int reference, JsonElement[] values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            JsonElement page = Assert.ContainsSingle(await ReadVariablesAsync(client, reference, index, 1)
                .ConfigureAwait(false));
            Assert.AreEqual(values[index].GetRawText(), page.GetRawText());
        }

        JsonElement last = Assert.ContainsSingle(await ReadVariablesAsync(client, reference, 1, int.MaxValue)
            .ConfigureAwait(false));
        Assert.AreEqual(values[1].GetRawText(), last.GetRawText());
        JsonElement remaining = Assert.ContainsSingle(await ReadVariablesAsync(client, reference, 1, 0)
            .ConfigureAwait(false));
        Assert.AreEqual(values[1].GetRawText(), remaining.GetRawText());
        Assert.IsEmpty(await ReadVariablesAsync(client, reference, values.Length, 1).ConfigureAwait(false));
        Assert.IsEmpty(await ReadVariablesAsync(client, reference, int.MaxValue, 1).ConfigureAwait(false));
    }

    private async Task<int> FindUnavailableLocalsFrameAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync("threads", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument threads = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(threads.RootElement, sequence, "threads", success: true);
        foreach (JsonElement thread in threads.RootElement.GetProperty("body").GetProperty("threads").EnumerateArray())
        {
            sequence = await client.SendRequestAsync("stackTrace",
                writer => WriteStackArguments(writer, thread.GetProperty("id").GetInt32()),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument stack = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(stack.RootElement, sequence, "stackTrace", success: true);
            foreach (JsonElement frame in stack.RootElement.GetProperty("body").GetProperty("stackFrames").EnumerateArray()
                .Where(static candidate => candidate.GetProperty("name").GetString() ==
                    "Csls.Debugger.Fixtures.CSharp.UnavailableLocalsFixture.Run"))
            {
                return frame.GetProperty("id").GetInt32();
            }
        }

        Assert.Fail($"No optimized fixture frame was found. {client.ProtocolTranscript}");
        return 0;
    }
}

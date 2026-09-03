using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP source retrieval through real Source Link metadata and HTTP.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Downloads checksum-valid Source Link content once and reuses the session cache.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SourceLinkProvidesVerifiedSourceContent()
    {
        SourceLinkTestServer server = SymbolFixtures.ValidSourceLinkServer;
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-sourcelink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        testDirectory = DebuggerTestPath.Canonicalize(testDirectory);
        try
        {
            await ExerciseSourceLinkAsync(
                SymbolFixtures.ValidSourceLinkProgramPath,
                testDirectory,
                server)
                .ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task ExerciseSourceLinkAsync(
        string programPath,
        string testDirectory,
        SourceLinkTestServer server)
    {
        const string documentPath = "/_/SourceLink/Program.cs";
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteSourceLinkLaunchArguments(
                writer,
                programPath,
                [Path.Join(testDirectory, "continue.signal"), "41", "source-link"],
                server.SourceLinkPattern),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, documentPath, 23),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpoint = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(breakpoint.RootElement, breakpointSequence, "setBreakpoints", success: true);
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        int threadId = await ReadInitialBreakpointStopAsync(
            client,
            configurationSequence,
            launchSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
        int sourceReference = await ReadSourceLinkReferenceAsync(client, threadId)
            .ConfigureAwait(false);
        await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
        await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
        Assert.AreEqual(1, server.RequestCount);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<int> ReadSourceLinkReferenceAsync(DapTestClient client, int threadId)
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
        JsonElement source = response.RootElement.GetProperty("body")
            .GetProperty("stackFrames")[0]
            .GetProperty("source");
        Assert.AreEqual("Source Link", source.GetProperty("origin").GetString());
        Assert.IsFalse(source.TryGetProperty("path", out _));
        return source.GetProperty("sourceReference").GetInt32();
    }

    private async Task AssertSourceLinkContentAsync(DapTestClient client, int sourceReference)
    {
        int sequence = await client.SendRequestAsync(
            "source",
            writer => WriteSourceArguments(writer, sourceReference),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "source", success: true);
        Assert.Contains(
            "answer++;",
            response.RootElement.GetProperty("body").GetProperty("content").GetString()!,
            StringComparison.Ordinal);
    }

    private static void WriteSourceArguments(Utf8JsonWriter writer, int sourceReference)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sourceReference", sourceReference);
        writer.WriteEndObject();
    }

    private static void WriteSourceLinkLaunchArguments(
        Utf8JsonWriter writer,
        string programPath,
        IReadOnlyList<string> arguments,
        string? sourceLinkPattern)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", false);
        writer.WriteString("program", programPath);
        writer.WriteStartArray("args");
        foreach (string argument in arguments)
        {
            writer.WriteStringValue(argument);
        }

        writer.WriteEndArray();
        if (sourceLinkPattern is not null)
        {
            writer.WriteStartObject("sourceLinkOptions");
            writer.WriteStartObject(sourceLinkPattern);
            writer.WriteBoolean("enabled", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP source retrieval through real Source Link metadata and HTTP.
/// </summary>
public sealed partial class DapSymbolTests
{
    /// <summary>
    /// Keeps checksum-valid Source Link references stable as local source changes under both source policies.
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
            int breakpointLine = FindSourceLine(
                await File.ReadAllLinesAsync(
                    SymbolFixtures.SourcePath,
                    TestContext.CancellationToken).ConfigureAwait(false),
                "answer++;");
            foreach (bool requireExactSource in new[] { true, false })
            {
                await ExerciseSourceLinkAsync(
                    SymbolFixtures.ValidSourceLinkProgramPath,
                    testDirectory,
                    server,
                    breakpointLine,
                    requireExactSource).ConfigureAwait(false);
            }

            Assert.AreEqual(2, server.RequestCount);
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
        SourceLinkTestServer server,
        int breakpointLine,
        bool requireExactSource)
    {
        string mappedPath = Path.Join(testDirectory, "Program.cs");
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
                server.SourceLinkPattern,
                mappedPath,
                requireExactSource),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, mappedPath, breakpointLine),
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
        await File.WriteAllTextAsync(mappedPath, "// Edited local source.\n", TestContext.CancellationToken).ConfigureAwait(false);
        JsonElement edited = await ReadLoadedSourceLinkDocumentAsync(client).ConfigureAwait(false);
        if (requireExactSource)
        {
            Assert.AreEqual(sourceReference, edited.GetProperty("sourceReference").GetInt32());
            Assert.IsFalse(edited.TryGetProperty("path", out _));
        }
        else
        {
            Assert.IsFalse(edited.TryGetProperty("sourceReference", out _));
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(mappedPath, edited.GetProperty("path").GetString()));
            Assert.IsFalse(edited.TryGetProperty("checksums", out _));
            Assert.Contains("unverified local source", edited.GetProperty("origin").GetString()!);
        }

        // A previously issued source reference continues to identify the original PDB content.
        await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
        File.Copy(SymbolFixtures.SourcePath, mappedPath, overwrite: true);
        JsonElement verified = await ReadLoadedSourceLinkDocumentAsync(client).ConfigureAwait(false);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(mappedPath, verified.GetProperty("path").GetString()));
        Assert.HasCount(1, verified.GetProperty("checksums").EnumerateArray());
        Assert.IsFalse(verified.TryGetProperty("origin", out _));
        File.Delete(mappedPath);
        Assert.AreEqual(sourceReference, await ReadSourceLinkReferenceAsync(client, threadId).ConfigureAwait(false));
        await AssertSourceLinkContentAsync(client, sourceReference).ConfigureAwait(false);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task<JsonElement> ReadLoadedSourceLinkDocumentAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync("loadedSources", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "loadedSources", success: true);
        return Assert.ContainsSingle(response.RootElement.GetProperty("body").GetProperty("sources").EnumerateArray()
            .Where(source => source.GetProperty("name").GetString() == "Program.cs")).Clone();
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
        string? sourceLinkPattern,
        string? mappedPath = null,
        bool requireExactSource = true)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("noDebug", false);
        writer.WriteString("program", programPath);
        writer.WriteBoolean("requireExactSource", requireExactSource);
        if (mappedPath is not null)
        {
            writer.WriteStartObject("sourceFileMap");
            writer.WriteString("/_/SourceLink/Program.cs", mappedPath);
            writer.WriteEndObject();
        }
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

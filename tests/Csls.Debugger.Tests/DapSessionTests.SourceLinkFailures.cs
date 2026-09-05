using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies Source Link trust and integrity failures over real DAP and HTTP transports.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Rejects implicit loopback Source Link access before opening an HTTP connection.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public Task SourceLinkRejectsImplicitPrivateNetworkAccess() =>
        ExerciseRejectedSourceLinkAsync(modifyContent: false, configureEndpoint: false);

    /// <summary>
    /// Rejects downloaded Source Link bytes that do not match the Portable PDB checksum.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public Task SourceLinkRejectsChecksumMismatch() =>
        ExerciseRejectedSourceLinkAsync(modifyContent: true, configureEndpoint: true);

    private async Task ExerciseRejectedSourceLinkAsync(
        bool modifyContent,
        bool configureEndpoint)
    {
        SourceLinkTestServer server = modifyContent
            ? SymbolFixtures.MismatchedSourceLinkServer
            : SymbolFixtures.ImplicitSourceLinkServer;
        string programPath = modifyContent
            ? SymbolFixtures.MismatchedSourceLinkProgramPath
            : SymbolFixtures.ImplicitSourceLinkProgramPath;
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-sourcelink-rejection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        testDirectory = DebuggerTestPath.Canonicalize(testDirectory);
        try
        {
            int breakpointLine = FindSourceLine(
                await File.ReadAllLinesAsync(
                    SymbolFixtures.SourcePath,
                    TestContext.CancellationToken).ConfigureAwait(false),
                "answer++;");
            await AssertRejectedSourceRequestAsync(
                programPath,
                testDirectory,
                configureEndpoint ? server.SourceLinkPattern : null,
                modifyContent ? "checksum" : "explicit",
                breakpointLine).ConfigureAwait(false);
            Assert.AreEqual(configureEndpoint ? 1 : 0, server.RequestCount);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task AssertRejectedSourceRequestAsync(
        string programPath,
        string testDirectory,
        string? configuredPattern,
        string expectedMessage,
        int breakpointLine)
    {
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        _ = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteSourceLinkLaunchArguments(
                writer,
                programPath,
                [Path.Join(testDirectory, "continue.signal"), "41", "source-link"],
                configuredPattern),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        int breakpointSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(
                writer,
                "/_/SourceLink/Program.cs",
                breakpointLine),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpoint = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            breakpoint.RootElement,
            breakpointSequence,
            "setBreakpoints",
            success: true);
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
        int sourceSequence = await client.SendRequestAsync(
            "source",
            writer => WriteSourceArguments(writer, sourceReference),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument source = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(source.RootElement, sourceSequence, "source", success: false);
        Assert.Contains(
            expectedMessage,
            source.RootElement.GetProperty("message").GetString()!,
            StringComparison.OrdinalIgnoreCase);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }
}

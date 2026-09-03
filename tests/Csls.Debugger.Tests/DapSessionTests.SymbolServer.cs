using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies identity-safe Portable PDB symbol-server resolution through DAP.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Downloads a matching Portable PDB once and reuses the validated symbol cache.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SymbolServerProvidesAndCachesMatchingPortablePdb()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-symbol-server-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            (string programPath, string sourcePath, string pdbPath) =
                await BuildSymbolServerFixtureAsync(testDirectory).ConfigureAwait(false);
            byte[] pdb = await File.ReadAllBytesAsync(
                pdbPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            string storeIndex = ReadPortablePdbStoreIndex(programPath);
            File.Delete(pdbPath);
            Assert.IsFalse(File.Exists(pdbPath));
            var server = new SymbolServerTestServer(storeIndex, pdb);
            await using ConfiguredAsyncDisposable serverDisposal = server.ConfigureAwait(false);
            server.Start();
            string cachePath = Path.Join(testDirectory, "symbol-cache");

            await ExerciseSymbolServerSessionAsync(
                programPath,
                sourcePath,
                cachePath,
                server.BaseUrl).ConfigureAwait(false);
            Assert.AreEqual(1, server.RequestCount);
            await ExerciseSymbolServerSessionAsync(
                programPath,
                sourcePath,
                cachePath,
                server.BaseUrl).ConfigureAwait(false);
            Assert.AreEqual(1, server.RequestCount);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private async Task ExerciseSymbolServerSessionAsync(
        string programPath,
        string sourcePath,
        string cachePath,
        string serverUrl)
    {
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
            writer => WriteSymbolServerLaunchArguments(
                writer,
                programPath,
                cachePath,
                serverUrl),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteSourceBreakpointArguments(writer, sourcePath, 8),
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
        await AssertSymbolServerModuleAsync(client, programPath, cachePath).ConfigureAwait(false);
        await AssertSymbolServerFrameAsync(client, threadId, sourcePath).ConfigureAwait(false);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task AssertSymbolServerModuleAsync(
        DapTestClient client,
        string programPath,
        string cachePath)
    {
        int sequence = await client.SendRequestAsync(
            "modules",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "modules", success: true);
        JsonElement module = response.RootElement.GetProperty("body").GetProperty("modules")
            .EnumerateArray()
            .Single(candidate => candidate.TryGetProperty("path", out JsonElement path) &&
                string.Equals(path.GetString(), programPath, StringComparison.Ordinal));
        string symbolPath = module.GetProperty("symbolFilePath").GetString()!;
        Assert.StartsWith(cachePath, symbolPath, StringComparison.Ordinal);
        Assert.IsTrue(File.Exists(symbolPath));
        Assert.AreEqual("Symbols loaded.", module.GetProperty("symbolStatus").GetString());
    }

    private async Task AssertSymbolServerFrameAsync(
        DapTestClient client,
        int threadId,
        string sourcePath)
    {
        int sequence = await client.SendRequestAsync(
            "stackTrace",
            writer => WriteThreadArguments(writer, threadId),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "stackTrace", success: true);
        JsonElement frame = response.RootElement.GetProperty("body").GetProperty("stackFrames")[0];
        Assert.AreEqual(sourcePath, frame.GetProperty("source").GetProperty("path").GetString());
        Assert.AreEqual(8, frame.GetProperty("line").GetInt32());
    }
}

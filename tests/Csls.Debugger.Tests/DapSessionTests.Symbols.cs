using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies DAP symbol discovery against real embedded Portable PDB metadata.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Reports loaded sources, embedded symbols, and zero-based executable locations.
    /// </summary>
    [TestMethod]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task EmbeddedSymbolsSupportDapDiscoveryRequests()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "test-assets",
            "Csls.Debugger.Fixtures.Embedded",
            "Program.cs");
        string programPath = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.Debugger.Fixtures.Embedded",
            "debug",
            "csls-debugger-fixture-embedded.dll");
        int breakpointLine = FindSourceLine(
            await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken)
                .ConfigureAwait(false),
            "int embeddedNumber = number + 1;");
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-dap-symbols-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            await ExerciseDapSymbolDiscoveryAsync(
                programPath,
                sourcePath,
                breakpointLine,
                testDirectory).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private async Task ExerciseDapSymbolDiscoveryAsync(
        string programPath,
        string sourcePath,
        int breakpointLine,
        string testDirectory)
    {
        Assert.IsFalse(File.Exists(Path.ChangeExtension(programPath, ".pdb")));
        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteZeroBasedInitializeArguments,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        JsonElement capabilities = initialize.RootElement.GetProperty("body");
        Assert.IsTrue(capabilities.GetProperty("supportsLoadedSourcesRequest").GetBoolean());
        Assert.IsTrue(
            capabilities.GetProperty("supportsBreakpointLocationsRequest").GetBoolean());

        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteMappedEmbeddedLaunchArguments(
                writer,
                programPath,
                [Path.Join(testDirectory, "continue.signal")],
                sourcePath),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int breakpointsSequence = await client.SendRequestAsync(
            "setBreakpoints",
            writer => WriteZeroBasedBreakpointArguments(writer, sourcePath, breakpointLine - 1),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument breakpoints = await client
            .ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        AssertResponse(
            breakpoints.RootElement,
            breakpointsSequence,
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

        await AssertEmbeddedModuleAsync(client, programPath).ConfigureAwait(false);
        int sourceReference = await AssertLoadedSourceAsync(client, sourcePath)
            .ConfigureAwait(false);
        await AssertEmbeddedSourceContentAsync(client, sourceReference).ConfigureAwait(false);
        await AssertEmbeddedStackSourceAsync(client, threadId, sourceReference)
            .ConfigureAwait(false);
        await AssertBreakpointLocationAsync(client, sourcePath, breakpointLine - 1)
            .ConfigureAwait(false);
        await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }

    private async Task DisconnectStoppedSessionAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "disconnect",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        while (true)
        {
            using JsonDocument response = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement root = response.RootElement;
            if (root.GetProperty("type").GetString() == "response" &&
                root.GetProperty("request_seq").GetInt32() == sequence)
            {
                AssertResponse(root, sequence, "disconnect", success: true);
                return;
            }
        }
    }

    private static void WriteZeroBasedInitializeArguments(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteString("adapterID", "csls-tests");
        writer.WriteBoolean("linesStartAt1", false);
        writer.WriteBoolean("columnsStartAt1", false);
        writer.WriteEndObject();
    }

    private static void WriteMappedEmbeddedLaunchArguments(
        Utf8JsonWriter writer,
        string programPath,
        IReadOnlyList<string> arguments,
        string sourcePath)
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
        writer.WriteStartObject("sourceFileMap");
        writer.WriteString(
            "C:\\agent\\_work\\Csls.Debugger.Fixtures.Embedded",
            Path.GetDirectoryName(sourcePath));
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteZeroBasedBreakpointArguments(
        Utf8JsonWriter writer,
        string sourcePath,
        int line)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("source");
        writer.WriteString("path", sourcePath);
        writer.WriteEndObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteNumber("line", line);
        writer.WriteNumber("column", 0);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteBreakpointLocationsArguments(
        Utf8JsonWriter writer,
        string sourcePath,
        int line)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("source");
        writer.WriteString("path", sourcePath);
        writer.WriteEndObject();
        writer.WriteNumber("line", line);
        writer.WriteEndObject();
    }
}

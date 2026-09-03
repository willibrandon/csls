using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source breakpoints and diagnostics after real tiered JIT promotion.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Binds and hits an IL breakpoint after its Release method has promoted to optimized code.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task SourceBreakpointBindsAfterTieredCompilationPromotion()
    {
        string artifactsPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-tiered-{Guid.NewGuid():N}");
        try
        {
            string programPath = await BuildJitFixtureAsync(artifactsPath).ConfigureAwait(false);
            string sourcePath = Path.Join(
                FindRepositoryRoot(),
                "test-assets",
                "Csls.Debugger.Fixtures.CSharp",
                "Program.cs");
            int breakpointLine = FindSourceLine(
                await File.ReadAllLinesAsync(
                    sourcePath,
                    TestContext.CancellationToken).ConfigureAwait(false),
                "int tieredAnswer = value + 1;");
            string warmSignal = Path.Join(artifactsPath, "warm.signal");
            string warmedSignal = Path.Join(artifactsPath, "warmed.signal");
            string executeSignal = Path.Join(artifactsPath, "execute.signal");
            string finishSignal = Path.Join(artifactsPath, "finish.signal");

            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await LaunchTieredFixtureAsync(
                client,
                programPath,
                warmSignal,
                warmedSignal,
                executeSignal,
                finishSignal).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                warmSignal,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            await WaitForSignalAsync(warmedSignal).ConfigureAwait(false);
            await PauseFixtureAsync(client).ConfigureAwait(false);

            int breakpointSequence = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, sourcePath, breakpointLine),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument breakpoints = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(
                breakpoints.RootElement,
                breakpointSequence,
                "setBreakpoints",
                success: true);
            JsonElement breakpoint = breakpoints.RootElement.GetProperty("body")
                .GetProperty("breakpoints").EnumerateArray().Single();
            Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean());
            Assert.AreEqual(breakpointLine, breakpoint.GetProperty("line").GetInt32());

            await File.WriteAllTextAsync(
                executeSignal,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int threadId = await ContinueToTieredBreakpointAsync(client).ConfigureAwait(false);
            (string frameName, string? framePath, int frameLine) = await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                "Csls.Debugger.Fixtures.CSharp.Program.IncrementTieredValue",
                frameName);
            Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, framePath));
            Assert.AreEqual(breakpointLine, frameLine);
            await AssertOptimizedModuleDiagnosticAsync(client, programPath).ConfigureAwait(false);

            await File.WriteAllTextAsync(
                finishSignal,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int finalContinueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadSuccessfulTerminationAsync(
                client,
                finalContinueSequence,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            if (Directory.Exists(artifactsPath))
            {
                Directory.Delete(artifactsPath, recursive: true);
            }
        }
    }

    private async Task LaunchTieredFixtureAsync(
        DapTestClient client,
        string programPath,
        string warmSignal,
        string warmedSignal,
        string executeSignal,
        string finishSignal)
    {
        int initializeSequence = await client.SendRequestAsync(
            "initialize",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialize = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(initialize.RootElement, initializeSequence, "initialize", success: true);
        int launchSequence = await client.SendRequestAsync(
            "launch",
            writer => WriteTieredLaunchArguments(
                writer,
                programPath,
                warmSignal,
                warmedSignal,
                executeSignal,
                finishSignal),
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument initialized = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertEvent(initialized.RootElement, "initialized");
        int configurationSequence = await client.SendRequestAsync(
            "configurationDone",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadTargetStartAsync(client, configurationSequence, launchSequence)
            .ConfigureAwait(false);
    }

    private static void WriteTieredLaunchArguments(
        Utf8JsonWriter writer,
        string programPath,
        string warmSignal,
        string warmedSignal,
        string executeSignal,
        string finishSignal)
    {
        writer.WriteStartObject();
        writer.WriteString("program", programPath);
        writer.WriteStartArray("args");
        writer.WriteStringValue("--tiered-compilation");
        writer.WriteStringValue(warmSignal);
        writer.WriteStringValue(warmedSignal);
        writer.WriteStringValue(executeSignal);
        writer.WriteStringValue(finishSignal);
        writer.WriteEndArray();
        writer.WriteStartObject("env");
        writer.WriteString("DOTNET_ReadyToRun", "0");
        writer.WriteString("DOTNET_TieredCompilation", "1");
        writer.WriteString("DOTNET_TC_AggressiveTiering", "1");
        writer.WriteEndObject();
        WriteDefaultSourceFileMap(writer);
        writer.WriteEndObject();
    }

    private async Task<int> ContinueToTieredBreakpointAsync(DapTestClient client)
    {
        int sequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        bool responseReceived = false;
        bool continuedReceived = false;
        int? threadId = null;
        while (!responseReceived || !continuedReceived || threadId is null)
        {
            using JsonDocument message = await client
                .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, sequence, "continue", success: true);
                responseReceived = true;
                continue;
            }

            string? eventName = root.GetProperty("event").GetString();
            if (eventName == "continued")
            {
                continuedReceived = true;
            }
            else if (eventName == "stopped")
            {
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("breakpoint", body.GetProperty("reason").GetString());
                threadId = body.GetProperty("threadId").GetInt32();
            }
        }

        return threadId.Value;
    }

    private async Task AssertOptimizedModuleDiagnosticAsync(
        DapTestClient client,
        string programPath)
    {
        int sequence = await client.SendRequestAsync(
            "modules",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client
            .ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "modules", success: true);
        JsonElement module = response.RootElement.GetProperty("body").GetProperty("modules")
            .EnumerateArray().Single(candidate =>
                candidate.TryGetProperty("path", out JsonElement path) &&
                DebuggerTestPath.AreEquivalent(path.GetString(), programPath));
        Assert.IsTrue(module.GetProperty("isOptimized").GetBoolean());
        Assert.IsFalse(module.GetProperty("isUserCode").GetBoolean());
        Assert.AreEqual("Symbols loaded.", module.GetProperty("symbolStatus").GetString());
    }
}

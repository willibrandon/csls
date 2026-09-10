using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies interpolated source logpoints through a real DAP target.
/// </summary>
public sealed partial class DapSessionTests
{
    private static readonly string[] s_expectedLogpointMessages =
    [
        "hit 2; next 3; braces {ok}\n",
        "hit 3; next 4; braces {ok}\n"
    ];

    /// <summary>
    /// Emits condition-matching expressions without exposing a debugger stop.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SourceLogpointInterpolatesAndContinues()
    {
        string testDirectory = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-logpoint-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        string signalPath = Path.Join(testDirectory, "continue.signal");
        string progressPath = Path.Join(testDirectory, "progress.txt");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int initializeSequence = await client.SendRequestAsync(
                "initialize",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialize = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                initialize.RootElement,
                initializeSequence,
                "initialize",
                success: true);
            Assert.IsTrue(initialize.RootElement.GetProperty("body")
                .GetProperty("supportsLogPoints")
                .GetBoolean());

            int launchSequence = await client.SendRequestAsync(
                "launch",
                writer => WriteLaunchArguments(
                    writer,
                    ResolveTestProcessHost(),
                    ["--debugger-hit-fixture", signalPath, progressPath, "3"],
                    wait: true,
                    noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument initialized = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertEvent(initialized.RootElement, "initialized");

            string sourcePath = Path.Join(
                FindRepositoryRoot(),
                "tests",
                "Csls.TestProcessHost",
                "DebuggerHitFixture.cs");
            string[] lines = await File.ReadAllLinesAsync(
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
            int logLine = FindSourceLine(lines, "GC.KeepAlive(observedHit);");
            int stopLine = FindSourceLine(lines, "Thread.Sleep(1);");
            int breakpointSequence = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteLogpointArguments(writer, sourcePath, logLine, stopLine),
                TestContext.CancellationToken).ConfigureAwait(false);
            using JsonDocument breakpoints = await client
                .ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            AssertResponse(
                breakpoints.RootElement,
                breakpointSequence,
                "setBreakpoints",
                success: true);
            int[] breakpointIds =
            [
                .. breakpoints.RootElement.GetProperty("body")
                    .GetProperty("breakpoints")
                    .EnumerateArray()
                    .Select(static breakpoint => breakpoint.GetProperty("id").GetInt32())
            ];
            Assert.HasCount(2, breakpointIds);

            int configurationSequence = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            bool configurationReceived = false;
            bool launchReceived = false;
            bool processReceived = false;
            bool stopped = false;
            var verifiedBreakpoints = new HashSet<int>();
            var logMessages = new List<string>();
            while (!configurationReceived || !launchReceived || !processReceived ||
                !stopped || verifiedBreakpoints.Count != 2 || logMessages.Count != 2)
            {
                using JsonDocument message = await client
                    .ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (root.GetProperty("type").GetString() == "response")
                {
                    int sequence = root.GetProperty("request_seq").GetInt32();
                    if (sequence == configurationSequence)
                    {
                        AssertResponse(
                            root,
                            configurationSequence,
                            "configurationDone",
                            success: true);
                        configurationReceived = true;
                    }
                    else if (sequence == launchSequence)
                    {
                        AssertResponse(root, launchSequence, "launch", success: true);
                        launchReceived = true;
                    }

                    continue;
                }

                string? eventName = root.GetProperty("event").GetString();
                if (eventName == "process")
                {
                    processReceived = true;
                }
                else if (eventName == "breakpoint")
                {
                    JsonElement breakpoint = root.GetProperty("body").GetProperty("breakpoint");
                    Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean());
                    _ = verifiedBreakpoints.Add(breakpoint.GetProperty("id").GetInt32());
                }
                else if (eventName == "output" && root.GetProperty("body")
                    .GetProperty("category").GetString() == "console")
                {
                    logMessages.Add(root.GetProperty("body").GetProperty("output").GetString()!);
                }
                else if (eventName == "stopped")
                {
                    Assert.AreEqual(
                        "breakpoint",
                        root.GetProperty("body").GetProperty("reason").GetString());
                    stopped = true;
                }
            }

            Assert.AreSequenceEqual(s_expectedLogpointMessages, logMessages);
            Assert.AreEqual(
                "3",
                await File.ReadAllTextAsync(
                    progressPath,
                    TestContext.CancellationToken).ConfigureAwait(false));
            await DisconnectStoppedSessionAsync(client).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(
                testDirectory,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static void WriteLogpointArguments(
        Utf8JsonWriter writer,
        string sourcePath,
        int logLine,
        int stopLine)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("source");
        writer.WriteString("path", sourcePath);
        writer.WriteEndObject();
        writer.WriteStartArray("breakpoints");
        writer.WriteStartObject();
        writer.WriteNumber("line", logLine);
        writer.WriteString("condition", "observedHit >= 2");
        writer.WriteString(
            "logMessage",
            "hit {observedHit}; next {observedHit + 1}; braces {{ok}}");
        writer.WriteEndObject();
        writer.WriteStartObject();
        writer.WriteNumber("line", stopLine);
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}

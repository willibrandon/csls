using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Compares Visual Basic named calls with a real stopped target's metadata.
/// </summary>
[TestClass]
public sealed class DapVisualBasicNamedArgumentTests : DapTestContext
{
    /// <summary>
    /// Uses case-insensitive names to bind static and instance calls in CLR order.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NamedArgumentsBindCaseInsensitivelyAgainstVisualBasicMetadata()
    {
        string releasePath = Path.Join(Path.GetTempPath(),
            $"csls-vb-named-arguments-{Guid.NewGuid():N}.signal");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        try
        {
            string source = Path.Join(FindRepositoryRoot(), "test-assets",
                "Csls.Debugger.Fixtures.VisualBasic", "Program.vb");
            int line = FindSourceLine(
                await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false),
                "While Not File.Exists(arguments(0))");
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(
                writer,
                DebuggerLanguageFixtures.GetProgramPath("Csls.Debugger.Fixtures.VisualBasic", "Debug"),
                [releasePath, "41", "named"], wait: true, noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int breakpoint = await client.SendRequestAsync("setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, source, line),
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, breakpoint, "setBreakpoints", success: true);
            }

            int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            int threadId = await ReadInitialBreakpointStopAsync(client, configuration, launch,
                TestContext.CancellationToken, TestContext).ConfigureAwait(false);
            JsonElement stack = await ReadDeepStackPageAsync(client, threadId, 0, 1).ConfigureAwait(false);
            JsonElement frame = Assert.ContainsSingle(stack.GetProperty("stackFrames").EnumerateArray());
            Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
            int frameId = frame.GetProperty("id").GetInt32();

            foreach ((string expression, string expected) in new[]
            {
                ("value.CombineNamed(SeCoNd:=answer, FIRST:=41)", "4183"),
                ("DebuggerFixtureValue.CombineNamedStatic(SeCoNd:=answer, FIRST:=41)", "4142")
            })
            {
                JsonElement result = await ReadEvaluationAsync(client, frameId, expression, success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(expected, result.GetProperty("result").GetString());
                using JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                AssertEvent(invalidated.RootElement, "invalidated");
            }

            await DisconnectAsync(client).ConfigureAwait(false);
            Assert.IsEmpty(client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(releasePath);
        }
    }
}

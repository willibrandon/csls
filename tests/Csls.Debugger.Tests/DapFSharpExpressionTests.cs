using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Evaluates F# source expressions against a real compiler-authored target.
/// </summary>
[TestClass]
public sealed class DapFSharpExpressionTests : DapTestContext
{
    /// <summary>
    /// Compares F# null tests with values computed by the target compiler.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NullTestsUseFSharpReferenceSemantics()
    {
        string releasePath = Path.Join(
            Path.GetTempPath(),
            $"csls-fsharp-null-tests-{Guid.NewGuid():N}.signal");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        try
        {
            string source = Path.Join(
                FindRepositoryRoot(),
                "test-assets",
                "Csls.Debugger.Fixtures.FSharp",
                "Program.fs");
            int line = FindSourceLine(
                await File.ReadAllLinesAsync(
                    source,
                    TestContext.CancellationToken).ConfigureAwait(false),
                "while not (File.Exists(arguments[0])) do");
            int initialize = await client.SendInitializeRequestAsync(
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(
                TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }

            int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(
                writer,
                DebuggerLanguageFixtures.GetProgramPath(
                    "Csls.Debugger.Fixtures.FSharp",
                    "Debug"),
                [releasePath, "41", "null"],
                wait: true,
                noDebug: false),
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument initialized = await client.ReadMessageAsync(
                TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertEvent(initialized.RootElement, "initialized");
            }

            int breakpoint = await client.SendRequestAsync(
                "setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, source, line),
                TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(
                TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, breakpoint, "setBreakpoints", success: true);
            }

            int configuration = await client.SendRequestAsync(
                "configurationDone",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            int threadId = await ReadInitialBreakpointStopAsync(
                client,
                configuration,
                launch,
                TestContext.CancellationToken,
                TestContext).ConfigureAwait(false);
            JsonElement stack = await ReadDeepStackPageAsync(client, threadId, 0, 1)
                .ConfigureAwait(false);
            JsonElement frame = Assert.ContainsSingle(
                stack.GetProperty("stackFrames").EnumerateArray());
            Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
            int frameId = frame.GetProperty("id").GetInt32();

            foreach ((string expression, string expected) in new[]
            {
                ("isNull nullReference", "true"),
                ("isNull referenceValue", "false"),
                ("nullReferenceOracle", "true"),
                ("nonNullReferenceOracle", "true")
            })
            {
                JsonElement result = await ReadEvaluationAsync(
                    client,
                    frameId,
                    expression,
                    success: true,
                    TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(expected, result.GetProperty("result").GetString());
                Assert.AreEqual("bool", result.GetProperty("type").GetString());
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

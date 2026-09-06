using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies response ordering while a real target repeatedly stops immediately after resumption.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Receives each continue and step response before its next stop while retaining exact target values.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ExecutionResponsesPrecedeImmediateBreakpointAndStepStops()
    {
        string sourcePath = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerResponseOrderFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(sourcePath, TestContext.CancellationToken).ConfigureAwait(false);
        int breakpointLine = FindSourceLine(lines, "total += index;");
        int nextLine = FindSourceLine(lines, "int expected = index");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        int thread = await LaunchToSourceBreakpointAsync(client, sourcePath, breakpointLine,
            ["--debugger-response-order-fixture"]).ConfigureAwait(false);
        for (int index = 0; index < 32; index++)
        {
            JsonElement before = await ReadTopSourceFrameAsync(client, thread).ConfigureAwait(false);
            Assert.AreEqual(breakpointLine, before.GetProperty("line").GetInt32());
            thread = await StepAndReadStopAsync(client, "next", thread, TestContext.CancellationToken)
                .ConfigureAwait(false);
            JsonElement after = await ReadTopSourceFrameAsync(client, thread).ConfigureAwait(false);
            Assert.AreEqual(nextLine, after.GetProperty("line").GetInt32());
            JsonElement total = await ReadEvaluationAsync(client, after.GetProperty("id").GetInt32(), "total",
                success: true, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual((index * (index + 1) / 2).ToString(CultureInfo.InvariantCulture),
                total.GetProperty("result").GetString());
            if (index < 31)
            {
                thread = await ContinueEntryToUserBreakpointAsync(client).ConfigureAwait(false);
            }
        }

        await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);
        int sequence = await client.SendRequestAsync("continue", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(client, sequence, TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed-IL instruction breakpoints through a real DAP process.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Stops at a disassembled managed-IL address and reports the exact breakpoint reason.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedInstructionBreakpointStopsAtDisassembledAddress()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerStepFixture.cs");
        string[] lines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int callLine = FindSourceLine(lines, "int combined = AddTwo(seed - 1) + AddTwo(seed);");
        int loopLine = FindSourceLine(lines, "while (!File.Exists(path))");
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-instruction-{Guid.NewGuid():N}.signal");
        try
        {
            (DapTestClient client, int threadId) = await StartStepTargetFixtureAsync(
                sourcePath,
                callLine,
                waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement frame = await ReadTopSourceFrameAsync(client, threadId)
                .ConfigureAwait(false);
            string frameReference = frame.GetProperty("instructionPointerReference").GetString()!;
            JsonElement[] instructions = await ReadDisassemblyAsync(
                client,
                frameReference,
                offset: 0,
                instructionOffset: 0,
                instructionCount: 64).ConfigureAwait(false);
            string targetAddress = instructions.First(instruction =>
                !instruction.TryGetProperty("presentationHint", out _) &&
                instruction.TryGetProperty("line", out JsonElement line) &&
                line.GetInt32() == loopLine).GetProperty("address").GetString()!;
            JsonElement direct = await SetInstructionBreakpointAsync(
                client,
                targetAddress,
                offset: 0,
                condition: "answer == 42",
                hitCondition: "3").ConfigureAwait(false);
            Assert.IsTrue(direct.GetProperty("verified").GetBoolean());
            Assert.AreEqual(
                targetAddress,
                direct.GetProperty("instructionReference").GetString());
            string baseAddress = instructions.First(instruction =>
                !instruction.TryGetProperty("presentationHint", out _))
                .GetProperty("address").GetString()!;
            long relativeOffset = ReadIlOffset(targetAddress) - ReadIlOffset(baseAddress);
            JsonElement breakpoint = await SetInstructionBreakpointAsync(
                client,
                frameReference,
                relativeOffset,
                condition: "answer == 42",
                hitCondition: "3").ConfigureAwait(false);
            Assert.IsTrue(breakpoint.GetProperty("verified").GetBoolean());
            Assert.AreEqual(
                frameReference,
                breakpoint.GetProperty("instructionReference").GetString());
            Assert.AreEqual(relativeOffset, breakpoint.GetProperty("offset").GetInt64());
            await ClearSourceBreakpointsAsync(client, sourcePath).ConfigureAwait(false);

            threadId = await ContinueToInstructionBreakpointAsync(client).ConfigureAwait(false);
            JsonElement stoppedFrame = await ReadTopSourceFrameAsync(client, threadId)
                .ConfigureAwait(false);
            Assert.AreEqual(loopLine, stoppedFrame.GetProperty("line").GetInt32());
            JsonElement[] current = await ReadDisassemblyAsync(
                client,
                stoppedFrame.GetProperty("instructionPointerReference").GetString()!,
                offset: 0,
                instructionOffset: 0,
                instructionCount: 1).ConfigureAwait(false);
            Assert.AreEqual(
                ReadIlOffset(targetAddress),
                ReadIlOffset(current[0].GetProperty("address").GetString()!));

            await ClearInstructionBreakpointsAsync(client).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                waitPath,
                string.Empty,
                TestContext.CancellationToken).ConfigureAwait(false);
            int continueSequence = await client.SendRequestAsync(
                "continue",
                WriteEmptyObject,
                TestContext.CancellationToken).ConfigureAwait(false);
            await ReadSuccessfulTerminationAsync(
                client,
                continueSequence,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(
                0,
                await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }

    private static uint ReadIlOffset(string address) => (uint)(ulong.Parse(
        address.AsSpan(2),
        NumberStyles.AllowHexSpecifier,
        CultureInfo.InvariantCulture) & uint.MaxValue);
}

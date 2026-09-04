using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies instruction-address retirement independently of logical stopped-frame continuity.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves a logical frame after evaluation while replacing its numeric and opaque IL references.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task FunctionEvaluationRetiresInstructionAddressesButPreservesLogicalFrame()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            JsonElement originalFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int frameId = originalFrame.GetProperty("id").GetInt32();
            string? originalReference = originalFrame.GetProperty("instructionPointerReference").GetString();
            Assert.IsNotNull(originalReference);
            JsonElement originalInstruction = Assert.ContainsSingle(await ReadDisassemblyAsync(
                client, originalReference, offset: 0, instructionOffset: 0, instructionCount: 1)
                .ConfigureAwait(false));
            string? originalAddress = originalInstruction.GetProperty("address").GetString();
            Assert.IsNotNull(originalAddress);
            Assert.StartsWith("0x", originalAddress);
            Assert.IsFalse(originalInstruction.TryGetProperty("presentationHint", out _));
            await AssertStaleDisassemblyRejectedAsync(client, "0x0000000000000000").ConfigureAwait(false);
            await AssertStaleDisassemblyRejectedAsync(client, "0xFFFFFFFF00000000").ConfigureAwait(false);

            JsonElement evaluation = await ReadEvaluationAsync(
                client, frameId, "localObject.NextNumber()", success: true, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual("43", evaluation.GetProperty("result").GetString());
            Assert.AreEqual("int", evaluation.GetProperty("type").GetString());
            using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertEvent(invalidated.RootElement, "invalidated");
                Assert.Contains("stacks", invalidated.RootElement.GetProperty("body").GetProperty("areas")
                    .EnumerateArray().Select(area => area.GetString()).ToArray());
            }

            await AssertStaleDisassemblyRejectedAsync(client, originalAddress).ConfigureAwait(false);
            await AssertStaleDisassemblyRejectedAsync(client, originalReference).ConfigureAwait(false);
            (_, JsonElement[] locals) = await ReadLogicalFrameLocalsAsync(client, frameId)
                .ConfigureAwait(false);
            Assert.AreEqual("43", Assert.ContainsSingle(locals.Where(local =>
                local.GetProperty("name").GetString() == "localNumber")).GetProperty("value").GetString());
            JsonElement refreshedFrame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            AssertSameLogicalFrame(originalFrame, refreshedFrame);
            string? currentReference = refreshedFrame.GetProperty("instructionPointerReference").GetString();
            Assert.IsNotNull(currentReference);
            Assert.AreNotEqual(originalReference, currentReference);
            JsonElement currentInstruction = Assert.ContainsSingle(await ReadDisassemblyAsync(
                client, currentReference, offset: 0, instructionOffset: 0, instructionCount: 1)
                .ConfigureAwait(false));
            string? currentAddress = currentInstruction.GetProperty("address").GetString();
            Assert.IsNotNull(currentAddress);
            Assert.AreNotEqual(originalAddress, currentAddress);
            Assert.AreEqual(ReadIlOffset(originalAddress), ReadIlOffset(currentAddress));
            Assert.AreEqual(originalInstruction.GetProperty("instruction").GetString(),
                currentInstruction.GetProperty("instruction").GetString());
            JsonElement roundTrip = Assert.ContainsSingle(await ReadDisassemblyAsync(
                client, currentAddress, offset: 0, instructionOffset: 0, instructionCount: 1)
                .ConfigureAwait(false));
            Assert.AreEqual(currentAddress, roundTrip.GetProperty("address").GetString());
            Assert.AreEqual(currentInstruction.GetProperty("instructionBytes").GetString(),
                roundTrip.GetProperty("instructionBytes").GetString());
            await AssertStaleDisassemblyRejectedAsync(client, originalAddress).ConfigureAwait(false);
            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies filesystem execution signals independently of debugger response delivery.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Preserves responses from fast target calls and rejects calls that never create the expected signal.
    /// </summary>
    /// <param name="createsSignal">Whether the target method writes its execution signal before returning.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task EvaluationSignalObservationPreservesCompletedResponse(bool createsSignal)
    {
        string waitPath = CreateResultsViewSignalPath();
        string signalPath = waitPath + ".evaluation";
        string releasePath = signalPath + ".release";
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            await File.WriteAllTextAsync(releasePath, "release", TestContext.CancellationToken)
                .ConfigureAwait(false);
            for (int iteration = 0; iteration < 8; iteration++)
            {
                JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
                int sequence = await client.SendRequestAsync("evaluate", writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                    writer.WriteString("expression", createsSignal
                        ? "localObject.WaitForDebuggerRelease()"
                        : "localObject.AddForDebugger(0)");
                    writer.WriteEndObject();
                }, TestContext.CancellationToken).ConfigureAwait(false);
                if (createsSignal)
                {
                    await client.WaitForTargetSignalAsync(signalPath, sequence, TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    Assert.IsTrue(File.Exists(signalPath));
                }
                else
                {
                    AssertFailedException failure = await Assert.ThrowsExactlyAsync<AssertFailedException>(
                        () => client.WaitForTargetSignalAsync(signalPath, sequence, TestContext.CancellationToken))
                        .ConfigureAwait(false);
                    Assert.Contains("ended before its execution signal", failure.Message);
                    Assert.IsFalse(File.Exists(signalPath));
                }

                using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false))
                {
                    AssertResponse(response.RootElement, sequence, "evaluate", success: true);
                    Assert.AreEqual("42", response.RootElement.GetProperty("body").GetProperty("result").GetString());
                }

                using (JsonDocument invalidated = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false))
                {
                    AssertEvent(invalidated.RootElement, "invalidated");
                }

                if (createsSignal)
                {
                    await client.WaitForTargetSignalAsync(signalPath, sequence, TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    Assert.AreEqual("started", await File.ReadAllTextAsync(signalPath, TestContext.CancellationToken)
                        .ConfigureAwait(false));
                }

                File.Delete(signalPath);
            }

            await FinishResultsViewSessionAsync(client).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(signalPath);
            File.Delete(releasePath);
        }
    }
}

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies that target termination preserves responses already owed to the client.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Acknowledges continuation even when the target immediately completes its managed entry point.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(64)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ContinueAcknowledgesImmediateTargetExit(int depth)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        (_, string sourcePath) = await StartDeepStackAsync(client, depth).ConfigureAwait(false);
        await FinishDeepStackAsync(client, sourcePath).ConfigureAwait(false);
    }

    /// <summary>
    /// Settles active evaluation and accepted inspection requests when their real target process dies.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task TargetExitDuringEvaluationCompletesAcceptedRequests()
    {
        string waitPath = CreateResultsViewSignalPath();
        try
        {
            DapTestClient client = await StartProxyFixtureAsync(waitPath, supportsInvalidatedEvent: false)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int processId = client.TargetProcessId ?? throw new InvalidOperationException("The target process was not reported.");
            Assert.IsGreaterThan(0, processId);
            Assert.AreNotEqual(client.HostProcessId, processId);
            using var target = Process.GetProcessById(processId);
            JsonElement frame = await GetFixtureFrameAsync(client).ConfigureAwait(false);
            int evaluation = await client.SendRequestAsync("evaluate", writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("expression", "localObject.WaitForDebuggerCancellation()");
                writer.WriteNumber("frameId", frame.GetProperty("id").GetInt32());
                writer.WriteEndObject();
            }, TestContext.CancellationToken).ConfigureAwait(false);
            await client.WaitForTargetSignalAsync(waitPath + ".evaluation", evaluation, TestContext.CancellationToken)
                .ConfigureAwait(false);
            Dictionary<int, string> outstanding = new() { [evaluation] = "evaluate" };
            for (int index = 0; index < 2; index++)
            {
                outstanding.Add(await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                    .ConfigureAwait(false), "threads");
            }

            // An untargeted cancel acknowledges the input barrier without canceling the active evaluation.
            int barrier = await client.SendRequestAsync("cancel", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using (JsonDocument acknowledgement = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false))
            {
                AssertResponse(acknowledgement.RootElement, barrier, "cancel", success: true);
            }

            target.Kill();
            await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            bool exited = false;
            bool terminated = false;
            while (outstanding.Count > 0 || !exited || !terminated)
            {
                using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement root = message.RootElement;
                if (root.GetProperty("type").GetString() == "response")
                {
                    int sequence = root.GetProperty("request_seq").GetInt32();
                    Assert.IsTrue(outstanding.Remove(sequence, out string? command), "Every accepted request must be answered once.");
                    Assert.IsNotNull(command);
                    AssertResponse(root, sequence, command, success: false);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
                    if (command == "evaluate")
                    {
                        Assert.Contains("target exited during managed function evaluation",
                            root.GetProperty("message").GetString()!);
                    }

                    continue;
                }

                string? eventName = root.GetProperty("event").GetString();
                if (eventName == "exited")
                {
                    Assert.IsFalse(exited);
                    Assert.AreNotEqual(0, root.GetProperty("body").GetProperty("exitCode").GetInt32());
                    exited = true;
                }
                else
                {
                    Assert.AreEqual("terminated", eventName);
                    Assert.IsTrue(exited);
                    Assert.IsFalse(terminated);
                    terminated = true;
                }
            }

            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
            File.Delete(waitPath + ".evaluation");
        }
    }
}

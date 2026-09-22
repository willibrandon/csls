using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Exercises repeated real DAP attachments to one independently running managed target.
/// </summary>
[TestClass]
public sealed class DapRepeatedAttachTests : DapTestContext
{
    /// <summary>
    /// Detaches and reattaches while the original managed process remains alive.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RepeatedAttachAfterDetachPreservesTarget()
    {
        string directory = Path.Join(Path.GetTempPath(), $"csls-reattach-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string releasePath = Path.Join(directory, "release");
        string program = ResolveTestProcessHost();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(program)
                ?? throw new InvalidOperationException("The test process host has no directory.")
        };
        start.ArgumentList.Add(program);
        start.ArgumentList.Add("--announce-and-spin-until-file");
        start.ArgumentList.Add(releasePath);

        try
        {
            using Process target = Process.Start(start)
                ?? throw new InvalidOperationException("The managed attach target did not start.");
            try
            {
                byte[] ready = new byte[5];
                await target.StandardOutput.BaseStream.ReadExactlyAsync(ready, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.AreEqual("ready", Encoding.ASCII.GetString(ready));

                for (int attempt = 0; attempt < 10; attempt++)
                {
                    await AttachAndDetachAsync(target.Id, inspect: attempt == 9).ConfigureAwait(false);
                    Assert.IsFalse(target.HasExited, $"The target exited after attach {attempt + 1}.");
                }

                await File.WriteAllTextAsync(releasePath, string.Empty, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                await target.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, target.ExitCode);
                TestContext.WriteLine(await target.StandardError.ReadToEndAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false));
            }
            finally
            {
                if (!target.HasExited)
                {
                    target.Kill(entireProcessTree: true);
                    await target.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(5))
                .ConfigureAwait(false);
        }
    }

    private async Task AttachAndDetachAsync(int processId, bool inspect)
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }

        int attach = await client.SendRequestAsync("attach", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("processId", processId);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }

        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
        }
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, attach, "attach", success: true);
        }
        using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(process.RootElement, "process");
            Assert.AreEqual(processId, process.RootElement.GetProperty("body")
                .GetProperty("systemProcessId").GetInt32());
        }

        if (inspect)
        {
            await InspectAttachedTargetAsync(client).ConfigureAwait(false);
        }

        int disconnect = await client.SendRequestAsync("disconnect", writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("terminateDebuggee", false);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, disconnect, "disconnect", success: true);
        }

        Assert.AreEqual(0, await client.WaitForProcessExitAsync(TestContext.CancellationToken)
            .ConfigureAwait(false));
        TestContext.WriteLine(client.Diagnostics.ToString());
    }

    private async Task InspectAttachedTargetAsync(DapTestClient client)
    {
        int pause = await client.SendRequestAsync("pause", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, pause, "pause", success: true);
        }
        int stoppedThread;
        using (JsonDocument stopped = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertEvent(stopped.RootElement, "stopped");
            stoppedThread = stopped.RootElement.GetProperty("body").GetProperty("threadId").GetInt32();
        }

        int threads = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken)
            .ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, threads, "threads", success: true);
            int[] threadIds = [.. response.RootElement.GetProperty("body")
                .GetProperty("threads").EnumerateArray()
                .Select(static thread => thread.GetProperty("id").GetInt32())];
            Assert.Contains(stoppedThread, threadIds);
        }

        int resume = await client.SendRequestAsync("continue", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", stoppedThread);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        bool responseReceived = false;
        bool continuedReceived = false;
        while (!responseReceived || !continuedReceived)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            if (message.RootElement.GetProperty("type").GetString() == "response")
            {
                AssertResponse(message.RootElement, resume, "continue", success: true);
                responseReceived = true;
            }
            else
            {
                AssertEvent(message.RootElement, "continued");
                continuedReceived = true;
            }
        }
    }
}

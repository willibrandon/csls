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
                    await AttachAndDetachAsync(target.Id).ConfigureAwait(false);
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

    private async Task AttachAndDetachAsync(int processId)
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
}

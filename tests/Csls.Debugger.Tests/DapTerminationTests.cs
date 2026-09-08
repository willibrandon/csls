using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed termination and restart against real child trees and unrelated sibling processes.
/// </summary>
[TestClass]
public sealed class DapTerminationTests : DapTestContext
{
    /// <summary>
    /// Retires every old target descendant and preserves unrelated processes across disconnect and restart.
    /// </summary>
    /// <param name="noDebug">Whether the target runs without a managed runtime connection.</param>
    /// <param name="restart">Whether to replace the target before disconnecting.</param>
    /// <param name="pause">Whether to stop the managed target before ending it.</param>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task EndingTargetRetiresOnlyItsProcessTree(bool noDebug, bool restart, bool pause)
    {
        string pipeName = $"csls-tree-{Guid.NewGuid():N}";
        using var firstRelease = new NamedPipeServerStream(pipeName, PipeDirection.Out, 2,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task firstConnected = firstRelease.WaitForConnectionAsync(TestContext.CancellationToken);
        using Process sibling = StartSibling();
        var targets = new List<Process>();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            await LaunchAsync(client, pipeName, noDebug).ConfigureAwait(false);
            await firstConnected.ConfigureAwait(false);
            int[] firstIds = await ReadTreeAsync(client, targets).ConfigureAwait(false);
            Assert.IsFalse(sibling.HasExited);
            if (pause)
            {
                int sequence = await client.SendRequestAsync("pause", WriteEmptyObject, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertResponse(response.RootElement, sequence, "pause", success: true);
                using JsonDocument stopped = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
                AssertEvent(stopped.RootElement, "stopped");
                Assert.IsTrue(stopped.RootElement.GetProperty("body").GetProperty("allThreadsStopped").GetBoolean());
            }
            // Each target must have exactly one available pipe instance when it connects.
            using NamedPipeServerStream? replacementRelease = restart
                ? new NamedPipeServerStream(pipeName, PipeDirection.Out, 2,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous)
                : null;
            if (replacementRelease is not null)
            {
                Task replacementConnected = replacementRelease.WaitForConnectionAsync(TestContext.CancellationToken);
                int sequence = await client.SendRequestAsync("restart", WriteEmptyObject, TestContext.CancellationToken)
                    .ConfigureAwait(false);
                await ReadTerminalResponseAsync(client, sequence, "restart", terminated: false).ConfigureAwait(false);
                await AssertTreeExitedAsync(targets).ConfigureAwait(false);
                Assert.IsFalse(sibling.HasExited);
                await replacementConnected.ConfigureAwait(false);
                int[] replacementIds = await ReadTreeAsync(client, targets).ConfigureAwait(false);
                Assert.IsEmpty(firstIds.Intersect(replacementIds));
            }
            int disconnect = await client.SendRequestAsync("disconnect", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            await ReadTerminalResponseAsync(client, disconnect, "disconnect", terminated: true).ConfigureAwait(false);
            await AssertTreeExitedAsync(targets).ConfigureAwait(false);
            Assert.IsFalse(sibling.HasExited);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(client.Diagnostics.ToString());
            sibling.StandardInput.Close();
            await sibling.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, sibling.ExitCode);
        }
        finally
        {
            foreach (Process process in targets.AsEnumerable().Reverse().Append(sibling))
            {
                using (process)
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task LaunchAsync(DapTestClient client, string pipeName, bool noDebug)
    {
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }
        int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
            ResolveTestProcessHost(), ["--debugger-process-tree", pipeName], wait: true, noDebug),
            TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument initialized = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(initialized.RootElement, "initialized");
        }
        int configuration = await client.SendRequestAsync("configurationDone", WriteEmptyObject, TestContext.CancellationToken)
            .ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, configuration, "configurationDone", success: true);
        }
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, launch, "launch", success: true);
        }
    }

    private async Task<int[]> ReadTreeAsync(DapTestClient client, List<Process> targets)
    {
        int processId;
        using (JsonDocument process = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertEvent(process.RootElement, "process");
            processId = process.RootElement.GetProperty("body").GetProperty("systemProcessId").GetInt32();
        }
        var output = new StringBuilder();
        do
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertEvent(message.RootElement, "output");
            JsonElement body = message.RootElement.GetProperty("body");
            Assert.AreEqual("stdout", body.GetProperty("category").GetString());
            _ = output.Append(body.GetProperty("output").GetString());
        }
        while (!output.ToString().EndsWith('\n'));
        int[] ids = [.. output.ToString().Trim().Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture))];
        Assert.HasCount(4, ids);
        Assert.AreEqual(processId, ids[0]);
        Assert.HasCount(4, ids.Distinct());
        foreach (int id in ids)
        {
            Assert.IsGreaterThan(0, id);
            targets.Add(Process.GetProcessById(id));
        }
        return ids;
    }

    private async Task ReadTerminalResponseAsync(DapTestClient client, int sequence, string command, bool terminated)
    {
        using (JsonDocument exited = await ReadTerminalMessageAsync(client).ConfigureAwait(false))
        {
            AssertEvent(exited.RootElement, "exited");
            Assert.IsTrue(exited.RootElement.GetProperty("body").GetProperty("exitCode").TryGetInt32(out _));
        }
        if (terminated)
        {
            using JsonDocument ended = await ReadTerminalMessageAsync(client).ConfigureAwait(false);
            AssertEvent(ended.RootElement, "terminated");
        }
        using JsonDocument response = await ReadTerminalMessageAsync(client).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, command, success: true);
    }

    private async Task<JsonDocument> ReadTerminalMessageAsync(DapTestClient client)
    {
        try
        {
            return await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DebuggerProcessDiagnostics.CaptureAsync(client.HostProcessId, TestContext).ConfigureAwait(false);
            throw;
        }
    }

    private async Task AssertTreeExitedAsync(IEnumerable<Process> targets)
    {
        foreach (Process process in targets)
        {
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(process.HasExited);
        }
    }

    private static Process StartSibling()
    {
        var startInfo = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(ResolveTestProcessHost());
        startInfo.ArgumentList.Add("--wait-for-standard-input");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The independent sibling did not start.");
    }
}

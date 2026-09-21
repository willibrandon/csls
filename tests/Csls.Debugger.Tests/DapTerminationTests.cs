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
            await LaunchAsync(client, ["--debugger-process-tree", pipeName], noDebug,
                terminateChildProcesses: true).ConfigureAwait(false);
            await WaitForTreeConnectionAsync(client, firstConnected).ConfigureAwait(false);
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
                await WaitForTreeConnectionAsync(client, replacementConnected).ConfigureAwait(false);
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

    private async Task WaitForTreeConnectionAsync(DapTestClient client, Task connected)
    {
        try
        {
            await connected.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DebuggerProcessDiagnostics.CaptureAsync(client.HostProcessId, TestContext).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Ends only the owned target by default or when child termination is explicitly disabled.
    /// </summary>
    /// <param name="noDebug">Whether the target runs without a managed runtime connection.</param>
    /// <param name="terminateChildProcesses">The optional explicit child-process policy.</param>
    [TestMethod]
    [DataRow(false, null)]
    [DataRow(false, false)]
    [DataRow(true, null)]
    [DataRow(true, false)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task EndingTargetPreservesDescendantsWithoutTreeTermination(
        bool noDebug,
        bool? terminateChildProcesses)
    {
        string pipeName = $"csls-tree-{Guid.NewGuid():N}";
        using var release = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = release.WaitForConnectionAsync(TestContext.CancellationToken);
        using Process sibling = StartSibling();
        var targets = new List<Process>();
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            string phase = "launching";
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(
                client, () => $"{Volatile.Read(ref phase)} ({client.ExitWaitState})");
            await LaunchAsync(client, ["--debugger-process-tree", pipeName], noDebug,
                terminateChildProcesses).ConfigureAwait(false);
            TestContext.WriteLine("Launched child-preserving target.");
            await WaitForTreeConnectionAsync(client, connected).ConfigureAwait(false);
            _ = await ReadTreeAsync(client, targets).ConfigureAwait(false);
            TestContext.WriteLine("Observed target and all descendants.");

            int disconnect = await client.SendRequestAsync(
                "disconnect", WriteEmptyObject, TestContext.CancellationToken).ConfigureAwait(false);
            Volatile.Write(ref phase, "reading disconnect response");
            TestContext.WriteLine("Sent disconnect request.");
            try
            {
                await ReadTerminalResponseAsync(client, disconnect, "disconnect", terminated: true)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TestContext.WriteLine($"Target exited: {targets[0].HasExited}; descendants exited: " +
                    string.Join(",", targets.Skip(1).Select(static process => process.HasExited)));
                throw;
            }
            TestContext.WriteLine("Received disconnect response.");

            await DebuggerProcessExit.WaitAsync(targets[0], TestContext.CancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref phase, "checking descendants");
            TestContext.WriteLine("Observed target exit.");
            Assert.IsTrue(targets[0].HasExited);
            foreach (Process descendant in targets.Skip(1))
            {
                Assert.IsFalse(descendant.HasExited, $"Child process {descendant.Id} should survive.");
            }
            Assert.IsFalse(sibling.HasExited);

            // Preserved descendants may retain the adapter's inherited stderr pipe.
            // Release these test-owned processes before requiring that pipe to reach EOF.
            foreach (Process descendant in targets.Skip(1).Reverse())
            {
                if (!descendant.HasExited)
                {
                    descendant.Kill(entireProcessTree: true);
                }

                await descendant.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            }

            Volatile.Write(ref phase, "waiting for adapter exit");
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Volatile.Write(ref phase, "checking adapter diagnostics");
            Assert.IsEmpty(client.Diagnostics.ToString());
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

    /// <summary>
    /// Completes target exit after draining available output even while an independent child retains the pipes.
    /// </summary>
    /// <param name="noDebug">Whether the target runs without a managed runtime connection.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NaturalTargetExitPreservesChildWithoutWaitingForInheritedOutput(bool noDebug)
    {
        string rootPipeName = $"csls-r-{Guid.NewGuid():N}";
        string childPipeName = $"csls-c-{Guid.NewGuid():N}";
        using var rootPipe = new NamedPipeServerStream(rootPipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var childPipe = new NamedPipeServerStream(childPipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task rootConnected = rootPipe.WaitForConnectionAsync(TestContext.CancellationToken);
        Task childConnected = childPipe.WaitForConnectionAsync(TestContext.CancellationToken);
        Process? root = null;
        Process? child = null;
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable clientDisposal = client.ConfigureAwait(false);
            using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
            await LaunchAsync(client,
                ["--debugger-inherited-output-root", rootPipeName, childPipeName], noDebug,
                terminateChildProcesses: null).ConfigureAwait(false);
            await Task.WhenAll(rootConnected, childConnected).ConfigureAwait(false);
            using var identities = new StreamReader(rootPipe, leaveOpen: true);
            string? announcement = await identities.ReadLineAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsNotNull(announcement);
            int[] ids = [.. announcement.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture))];
            Assert.HasCount(2, ids);
            root = Process.GetProcessById(ids[0]);
            child = Process.GetProcessById(ids[1]);
            _ = root.SafeHandle;
            _ = child.SafeHandle;

            await rootPipe.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
            var output = new StringBuilder();
            bool exited = false;
            bool terminated = false;
            while (!terminated)
            {
                using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                JsonElement envelope = message.RootElement;
                Assert.AreEqual("event", envelope.GetProperty("type").GetString());
                switch (envelope.GetProperty("event").GetString())
                {
                    case "process":
                        Assert.AreEqual(ids[0], envelope.GetProperty("body").GetProperty("systemProcessId").GetInt32());
                        break;
                    case "output":
                        JsonElement body = envelope.GetProperty("body");
                        if (body.GetProperty("category").GetString() == "stdout")
                        {
                            _ = output.Append(body.GetProperty("output").GetString());
                        }
                        break;
                    case "exited":
                        Assert.IsFalse(exited);
                        Assert.AreEqual(0, envelope.GetProperty("body").GetProperty("exitCode").GetInt32());
                        exited = true;
                        break;
                    case "terminated":
                        Assert.IsTrue(exited);
                        terminated = true;
                        break;
                    default:
                        Assert.Fail($"Unexpected debugger event: {envelope}");
                        break;
                }
            }

            Assert.Contains(rootPipeName, output.ToString());
            Assert.IsTrue(root.HasExited);
            Assert.IsFalse(child.HasExited);
            Assert.AreEqual(0, await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsEmpty(client.Diagnostics.ToString());
        }
        finally
        {
            foreach (Process? process in new[] { child, root })
            {
                if (process is null)
                {
                    continue;
                }
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

    private async Task LaunchAsync(
        DapTestClient client,
        IReadOnlyList<string> arguments,
        bool noDebug,
        bool? terminateChildProcesses)
    {
        int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
        {
            AssertResponse(response.RootElement, initialize, "initialize", success: true);
        }
        int launch = await client.SendRequestAsync("launch", writer => WriteLaunchArguments(writer,
            ResolveTestProcessHost(), arguments, wait: true, noDebug,
            terminateChildProcesses: terminateChildProcesses),
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
            var process = Process.GetProcessById(id);
            targets.Add(process);
            // Keep the original process object alive across termination and Windows PID reuse.
            _ = process.SafeHandle;
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
            await DebuggerProcessExit.WaitAsync(process, TestContext.CancellationToken).ConfigureAwait(false);
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

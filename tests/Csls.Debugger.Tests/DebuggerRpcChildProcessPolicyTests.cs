using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies launched child-process ownership through the real private debugger transport.
/// </summary>
[TestClass]
public sealed class DebuggerRpcChildProcessPolicyTests : DapTestContext
{
    /// <summary>
    /// Applies the selected child-process policy when a managed RPC session ends.
    /// </summary>
    /// <param name="terminateChildProcesses">Whether descendants are included in termination.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(ConditionMode.Exclude, OperatingSystems.Windows)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task TerminateRespectsChildProcessPolicy(bool terminateChildProcesses)
    {
        string pipeName = $"cr-{Guid.NewGuid():N}";
        using var release = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = release.WaitForConnectionAsync(TestContext.CancellationToken);
        using var outputChanged = new SemaphoreSlim(0);
        var targets = new List<Process>();
        try
        {
            DebuggerWorkerTestSession worker = await DebuggerWorkerTestSession
                .StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable workerCleanup = worker.ConfigureAwait(false);
            DebuggerRpcClient client = worker.Client;
            client.ResourceChanged += (_, change) =>
            {
                if (change.Kind.HasFlag(DebuggerResourceChangeKind.Output))
                {
                    _ = outputChanged.Release();
                }
            };

            DebugSessionSnapshot launched = await client.LaunchAsync(new DebugLaunchRequest
            {
                Program = ResolveTestProcessHost(),
                WorkingDirectory = FindRepositoryRoot(),
                Arguments = ["--debugger-process-tree", pipeName],
                TerminateChildProcesses = terminateChildProcesses
            }, TestContext.CancellationToken).ConfigureAwait(false);
            await connected.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);

            var output = new StringBuilder();
            long cursor = 0;
            while (!output.ToString().Contains('\n', StringComparison.Ordinal))
            {
                DebugOutputPage page = await client.GetOutputAsync(
                    new DebugOutputRequest(cursor, 64), TestContext.CancellationToken).ConfigureAwait(false);
                cursor = page.NextSequence;
                foreach (DebugOutputEntry entry in page.Entries.Where(
                    static entry => entry.Category == DebugOutputCategory.StandardOutput))
                {
                    _ = output.Append(entry.Output);
                }

                if (!output.ToString().Contains('\n', StringComparison.Ordinal))
                {
                    await outputChanged.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                }
            }

            int[] ids = [.. output.ToString().Trim().Split(',')
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))];
            Assert.HasCount(4, ids);
            Assert.AreEqual(launched.ProcessId, ids[0]);
            foreach (Process process in ids.Select(Process.GetProcessById))
            {
                targets.Add(process);
                _ = process.SafeHandle;
            }

            DebugSessionSnapshot ended = await client.TerminateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.AreEqual(DebugSessionState.Terminated, ended.State);
            await DebuggerProcessExit.WaitAsync(targets[0], TestContext.CancellationToken)
                .ConfigureAwait(false);
            Assert.IsTrue(targets[0].HasExited);
            foreach (Process child in targets.Skip(1))
            {
                if (terminateChildProcesses)
                {
                    await DebuggerProcessExit.WaitAsync(child, TestContext.CancellationToken)
                        .ConfigureAwait(false);
                    Assert.IsTrue(child.HasExited);
                }
                else
                {
                    Assert.IsFalse(child.HasExited);
                }
            }
        }
        finally
        {
            foreach (Process process in targets.AsEnumerable().Reverse())
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
}

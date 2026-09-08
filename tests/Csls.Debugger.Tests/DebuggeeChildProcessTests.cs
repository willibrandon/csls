using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies operating-system child discovery and subtree termination through independently owned processes.
/// </summary>
[TestClass]
public sealed class DebuggeeChildProcessTests
{
    /// <summary>
    /// Gets the framework context used for cancellation and diagnostic evidence.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Finds exact direct child relationships and terminates descendants while preserving their root and a sibling.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task DiscoveryAndTerminationPreserveProcessOwnership()
    {
        string pipeName = $"csls-tree-{Guid.NewGuid():N}";
        using var release = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Task connected = release.WaitForConnectionAsync(TestContext.CancellationToken);
        using Process sibling = StartFixture("--wait-for-standard-input");
        using Process root = StartFixture("--debugger-process-tree", pipeName);
        try
        {
            await connected.ConfigureAwait(false);
            string? ready = await root.StandardOutput.ReadLineAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(ready);
            int[] ids = [.. ready.Split(',').Select(value => int.Parse(value, CultureInfo.InvariantCulture))];
            Assert.HasCount(4, ids);
            Assert.AreEqual(root.Id, ids[0]);
            Assert.HasCount(4, ids.Distinct());
            Assert.AreSequenceEqual(new[] { ids[1], ids[3] }.Order(), DebuggeeChildProcesses.GetIds(root.Id));
            Assert.AreSequenceEqual(new[] { ids[2] }, DebuggeeChildProcesses.GetIds(ids[1]));
            Assert.IsEmpty(DebuggeeChildProcesses.GetIds(ids[2]));
            Assert.IsEmpty(DebuggeeChildProcesses.GetIds(ids[3]));
            Assert.IsEmpty(DebuggeeChildProcesses.GetIds(sibling.Id));
            using var branch = Process.GetProcessById(ids[1]);
            using var grandchild = Process.GetProcessById(ids[2]);
            using var leaf = Process.GetProcessById(ids[3]);

            DebuggeeChildProcesses.Terminate(root.Id);

            await branch.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await grandchild.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await leaf.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsFalse(root.HasExited);
            Assert.IsFalse(sibling.HasExited);
            Assert.IsEmpty(DebuggeeChildProcesses.GetIds(root.Id));
            await release.WriteAsync(new byte[] { 1 }, TestContext.CancellationToken).ConfigureAwait(false);
            await root.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, root.ExitCode);
            Assert.IsEmpty(await root.StandardError.ReadToEndAsync(TestContext.CancellationToken).ConfigureAwait(false));
            sibling.StandardInput.Close();
            await sibling.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, sibling.ExitCode);
        }
        finally
        {
            await EndFixtureAsync(root).ConfigureAwait(false);
            await EndFixtureAsync(sibling).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects process identifiers that could select an operating-system process group.
    /// </summary>
    /// <param name="processId">The invalid parent identifier.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MinValue)]
    public void InvalidParentIdentifiersAreRejected(int processId)
    {
        ArgumentOutOfRangeException exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => DebuggeeChildProcesses.GetIds(processId));
        Assert.AreEqual("parentProcessId", exception.ParamName);
        exception = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => DebuggeeChildProcesses.Terminate(processId));
        Assert.AreEqual("parentProcessId", exception.ParamName);
    }

    /// <summary>
    /// Preserves the public Windows snapshot layout and pointer-sized field alignment.
    /// </summary>
    [TestMethod]
    public void WindowsSnapshotMatchesNativeLayout()
    {
        Assert.AreEqual(IntPtr.Size == 8 ? 568 : 556, Marshal.SizeOf<WindowsProcessSnapshotEntry>());
        Assert.AreEqual(IntPtr.Size == 8 ? 32 : 24,
            Marshal.OffsetOf<WindowsProcessSnapshotEntry>(nameof(WindowsProcessSnapshotEntry._parentProcessId)).ToInt32());
    }

    private static Process StartFixture(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(Path.Join(DebuggerTestEnvironment.FindRepositoryRoot(),
            "artifacts", "bin", "Csls.TestProcessHost", "debug", "csls-test-process-host.dll"));
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The owned process fixture did not start.");
    }

    private static async Task EndFixtureAsync(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

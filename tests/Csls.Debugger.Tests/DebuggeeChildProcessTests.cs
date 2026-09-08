using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
            // Retain exact process handles while the children are alive, before requesting their termination.
            _ = branch.SafeHandle;
            _ = grandchild.SafeHandle;
            _ = leaf.SafeHandle;

            DebuggeeChildProcesses.Terminate(root.Id);

            await DebuggerProcessExit.WaitAsync(branch, TestContext.CancellationToken).ConfigureAwait(false);
            await DebuggerProcessExit.WaitAsync(grandchild, TestContext.CancellationToken).ConfigureAwait(false);
            await DebuggerProcessExit.WaitAsync(leaf, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.IsTrue(branch.HasExited);
            Assert.IsTrue(grandchild.HasExited);
            Assert.IsTrue(leaf.HasExited);
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
    /// Preserves discovery when a process exits after its procfs stat reader has been acquired.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task LinuxProcessExitAfterStatOpenRetiresRecord()
    {
        using Process process = StartFixture("--wait-for-standard-input");
        try
        {
            string path = $"/proc/{process.Id}/stat";
            using (var live = new StreamReader(path))
            {
                Assert.AreEqual(Environment.ProcessId, DebuggeeChildProcesses.ReadLinuxParent(live, process.Id));
            }
            using var retired = new StreamReader(path);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, process.ExitCode);
            Assert.IsNull(DebuggeeChildProcesses.ReadLinuxParent(retired, process.Id));
            Assert.IsTrue(retired.BaseStream.CanRead);
            Assert.IsEmpty(DebuggeeChildProcesses.GetIds(process.Id));
        }
        finally
        {
            await EndFixtureAsync(process).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects older and identical process identities while preserving query ownership through child exit.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [SupportedOSPlatform("windows")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task WindowsCreationTimesExcludeOlderProcessIdentities()
    {
        using var parent = Process.GetCurrentProcess();
        using Process child = StartFixture("--wait-for-standard-input");
        try
        {
            using SafeProcessHandle parentIdentity = WindowsProcessIdentity.Open(parent.Id);
            using SafeProcessHandle childIdentity = WindowsProcessIdentity.Open(child.Id);
            Assert.IsFalse(parentIdentity.IsInvalid);
            Assert.IsFalse(childIdentity.IsInvalid);
            Assert.IsGreaterThan(parent.StartTime.ToUniversalTime(), child.StartTime.ToUniversalTime());
            Assert.IsTrue(WindowsProcessIdentity.WasCreatedAfter(childIdentity, parentIdentity));
            Assert.IsFalse(WindowsProcessIdentity.WasCreatedAfter(parentIdentity, childIdentity),
                "A process older than the selected parent cannot belong to that parent's process tree.");
            Assert.IsFalse(WindowsProcessIdentity.WasCreatedAfter(childIdentity, childIdentity),
                "The parent's own identity cannot be classified as one of its children.");
            Assert.IsTrue(WindowsProcessIdentity.HasParent(childIdentity, parent.Id));
            Assert.IsFalse(WindowsProcessIdentity.HasParent(parentIdentity, child.Id));
            await AssertWindowsSiblingOwnershipAsync(child.Id, childIdentity).ConfigureAwait(false);

            child.StandardInput.Close();
            await DebuggerProcessExit.WaitAsync(child, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, child.ExitCode);
            Assert.IsTrue(WindowsProcessIdentity.WasCreatedAfter(childIdentity, parentIdentity));
            Assert.IsTrue(WindowsProcessIdentity.HasParent(childIdentity, parent.Id));
            Assert.IsFalse(parentIdentity.IsClosed);
            Assert.IsFalse(childIdentity.IsClosed);
            Assert.IsEmpty(WindowsProcessSnapshot.GetChildren(child.Id));
        }
        finally
        {
            await EndFixtureAsync(child).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertWindowsSiblingOwnershipAsync(int childProcessId, SafeProcessHandle childIdentity)
    {
        using Process sibling = StartFixture("--wait-for-standard-input");
        try
        {
            using SafeProcessHandle siblingIdentity = WindowsProcessIdentity.Open(sibling.Id);
            Assert.IsTrue(WindowsProcessIdentity.WasCreatedAfter(siblingIdentity, childIdentity));
            Assert.IsFalse(WindowsProcessIdentity.HasParent(siblingIdentity, childProcessId),
                "A newer sibling is not a child of the earlier process.");
        }
        finally
        {
            await EndFixtureAsync(sibling).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reports malformed process records instead of treating them as retired processes.
    /// </summary>
    /// <param name="record">The hostile process record written through a real file boundary.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("123")]
    [DataRow("123 (target) S")]
    [DataRow("123 (target) S invalid 0")]
    public void MalformedLinuxProcessRecordsRemainErrors(string record)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, record);
            using var reader = new StreamReader(path);
            IOException failure = Assert.ThrowsExactly<IOException>(() => DebuggeeChildProcesses.ReadLinuxParent(reader, 123));
            Assert.Contains("123", failure.Message);
            Assert.IsTrue(reader.BaseStream.CanRead);
        }
        finally
        {
            File.Delete(path);
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
        Assert.AreEqual(6 * IntPtr.Size, Marshal.SizeOf<WindowsProcessBasicInformation>());
        Assert.AreEqual(5 * IntPtr.Size,
            Marshal.OffsetOf<WindowsProcessBasicInformation>(nameof(WindowsProcessBasicInformation._parentProcessId)).ToInt32());
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

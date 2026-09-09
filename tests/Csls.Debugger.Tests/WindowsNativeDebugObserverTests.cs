using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies external native observation preserves runtime dispatch, fault identity, and caller-owned process cleanup.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class WindowsNativeDebugObserverTests : DapTestContext
{
    /// <summary>
    /// Preserves CLR translation and handling of real null dereferences while bounding first-chance reports.
    /// </summary>
    /// <param name="mode">The managed exception scenario.</param>
    /// <param name="handled">The number of actual managed handlers that must execute.</param>
    /// <param name="reports">The number of first-chance records retained by the observer.</param>
    [TestMethod]
    [DataRow("managed", 1, 1)]
    [DataRow("bounded", 12, 8)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeObservationPreservesManagedExceptionHandling(string mode, int handled, int reports)
    {
        var progress = new ConcurrentQueue<string>();
        (int processId, int exitCode, string output, string error) = await DebuggerTestProcess.RunWithIdentityAsync(
            CreateStart(mode), TestContext.CancellationToken, progress.Enqueue, observeNativeExceptions: true)
            .ConfigureAwait(false);
        Assert.AreEqual(0, exitCode, error);
        Assert.AreSequenceEqual(Enumerable.Repeat(nameof(NullReferenceException), handled), Lines(output));
        string[] records = Lines(error);
        Assert.HasCount(reports, records);
        Assert.AreSequenceEqual(records, progress.Where(IsFault));
        foreach (string record in records)
        {
            AssertEvidence(record, processId, firstChance: true, nativeImage: false);
        }
    }

    /// <summary>
    /// Retains the terminal fault and exit code even after earlier managed exceptions exhaust the report budget.
    /// </summary>
    /// <param name="mode">The terminal native exception scenario.</param>
    /// <param name="reports">The expected number of retained exceptions, including the terminal fault.</param>
    /// <param name="handled">The managed exceptions handled before the terminal native fault.</param>
    [TestMethod]
    [DataRow("fatal", 1, 0)]
    [DataRow("bounded-fatal", 9, 12)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeFaultPreservesExceptionEvidenceAndExitStatus(string mode, int reports, int handled)
    {
        (int processId, int exitCode, string output, string error) = await DebuggerTestProcess.RunWithIdentityAsync(
            CreateStart(mode), TestContext.CancellationToken, observeNativeExceptions: true).ConfigureAwait(false);
        Assert.AreEqual(unchecked((int)0xc0000005), exitCode, error);
        Assert.AreSequenceEqual(Enumerable.Repeat(nameof(NullReferenceException), handled), Lines(output));
        string[] records = [.. Lines(error).Where(IsFault)];
        Assert.HasCount(reports, records, error);
        foreach (string record in records[..^1])
        {
            AssertEvidence(record, processId, firstChance: true, nativeImage: mode == "fatal");
        }
        AssertEvidence(records[^1], processId, firstChance: true, nativeImage: true);
    }

    /// <summary>
    /// Retains a native second-chance exception and allows Windows to terminate the process with its original status.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task SecondChanceFaultPreservesExceptionEvidenceAndExitStatus()
    {
        (int processId, int exitCode, string output, string error) = await DebuggerTestProcess.RunWithIdentityAsync(
            CreateStart("second-chance"), TestContext.CancellationToken, observeNativeExceptions: true).ConfigureAwait(false);
        Assert.AreEqual(unchecked((int)0xc0000005), exitCode, error);
        Assert.AreEqual(string.Empty, output);
        string record = Assert.ContainsSingle(Lines(error).Where(IsFault));
        AssertEvidence(record, processId, firstChance: false, nativeImage: false);
        Assert.Contains(" operation=1 address=0x12345678.", record);
    }

    /// <summary>
    /// Detaches native observation and reaps the exact retained child when capture is canceled while it waits.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task CancellationReapsObservedProcess()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var announced = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<(int ProcessId, int ExitCode, string Output, string Error)> running = DebuggerTestProcess.RunWithIdentityAsync(
            CreateStart("waiting"), cancellation.Token,
            line => announced.TrySetResult(int.Parse(line, CultureInfo.InvariantCulture)), observeNativeExceptions: true);
        try
        {
            int id = await announced.Task.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using var child = Process.GetProcessById(id);
            _ = child.SafeHandle;
            Assert.IsFalse(child.HasExited);
            await cancellation.CancelAsync().ConfigureAwait(false);
            OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await running.WaitAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(cancellation.Token, exception.CancellationToken);
            Assert.IsTrue(child.HasExited, "Cancellation must reap the process whose kernel handle the test retains.");
            Assert.IsTrue(await Task.Run(() => child.WaitForExit(0), TestContext.CancellationToken).ConfigureAwait(false),
                "The owned process object must be signaled before capture completes.");
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            Task completion = running;
            await completion.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Preserves an observation sink failure and reaps its child after continuing the outstanding native event.
    /// </summary>
    /// <param name="mode">Whether the rejected fault is published at the exception or the process-exit event.</param>
    [TestMethod]
    [DataRow("fatal")]
    [DataRow("bounded-fatal")]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ReportFailureReapsObservedProcess(string mode)
    {
        using var children = new DisposableCollection<Process>();
        Process? retained = null;
        var failure = new IOException("The test-owned native diagnostic sink rejected its record.");
        IOException exception = await Assert.ThrowsExactlyAsync<IOException>(async () =>
            await DebuggerTestProcess.RunWithIdentityAsync(CreateStart(mode), TestContext.CancellationToken, line =>
            {
                if (IsFault(line))
                {
                    if (retained is null)
                    {
                        string identity = line.Split(' ')[3]["process=".Length..];
                        retained = children.Acquire(() => Process.GetProcessById(int.Parse(identity, CultureInfo.InvariantCulture)));
                        _ = retained.SafeHandle;
                    }
                    if (line.Contains(" address=0x12345678.", StringComparison.Ordinal))
                    {
                        throw failure;
                    }
                }
            }, observeNativeExceptions: true).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreSame(failure, exception);
        Assert.IsNotNull(retained);
        Assert.IsTrue(retained.HasExited);
        Assert.IsTrue(await Task.Run(() => retained.WaitForExit(0), TestContext.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Detaches the native observer and reaps its waiting child when redirected output reporting fails.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task OutputFailureDetachesAndReapsObservedProcess()
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var children = new DisposableCollection<Process>();
        var failure = new IOException("The observed child's output sink rejected its announcement.");
        Process? retained = null;
        Task running = DebuggerTestProcess.RunWithIdentityAsync(CreateStart("waiting"), operation.Token, line =>
        {
            retained = children.Acquire(() => Process.GetProcessById(int.Parse(line, CultureInfo.InvariantCulture)));
            _ = retained.SafeHandle;
            throw failure;
        }, observeNativeExceptions: true);
        try
        {
            IOException observed = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);
            Assert.AreSame(failure, observed);
            Assert.IsFalse(operation.IsCancellationRequested);
            Assert.IsNotNull(retained);
            Assert.IsTrue(await Task.Run(() => retained.WaitForExit(0), TestContext.CancellationToken).ConfigureAwait(false),
                "The native observer must release the process before cleanup completes.");
        }
        finally
        {
            await operation.CancelAsync().ConfigureAwait(false);
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static ProcessStartInfo CreateStart(string mode)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
        start.ArgumentList.Add(ResolveTestProcessHost());
        start.ArgumentList.Add("--windows-observed");
        start.ArgumentList.Add("--windows-native-fault");
        start.ArgumentList.Add(mode);
        return start;
    }

    private static bool IsFault(string line) => line.StartsWith("Native collector exception:", StringComparison.Ordinal);

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static void AssertEvidence(string line, int processId, bool firstChance, bool nativeImage)
    {
        Assert.IsTrue(IsFault(line), line);
        var values = line["Native collector exception: ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.TrimEnd('.').Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1]);
        Assert.AreEqual(processId, int.Parse(values["process"], CultureInfo.InvariantCulture));
        Assert.AreEqual(firstChance, bool.Parse(values["first-chance"]));
        Assert.AreEqual(0xc0000005UL, Number("code"));
        Assert.IsGreaterThan(0, int.Parse(values["thread"], CultureInfo.InvariantCulture));
        Assert.IsGreaterThan(0UL, Number("instruction"));
        byte[] context = Convert.FromHexString(values["context-prefix"]);
        Assert.HasCount(272, context);
        int instructionOffset = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 248,
            Architecture.Arm64 => 264,
            Architecture.X86 => 184,
            _ => throw new PlatformNotSupportedException()
        };
        ulong instruction = IntPtr.Size == 8
            ? BitConverter.ToUInt64(context, instructionOffset) : BitConverter.ToUInt32(context, instructionOffset);
        Assert.AreEqual(Number("instruction"), instruction, "The retained registers must belong to the faulting instruction.");
        if (nativeImage)
        {
            Assert.AreEqual(1UL, ulong.Parse(values["operation"], CultureInfo.InvariantCulture));
            Assert.AreEqual(0x12345678UL, Number("address"));
            Assert.IsNotEmpty(values["module"]);
            Assert.IsNotEmpty(values["version"]);
            Assert.IsGreaterThan(0UL, Number("base"));
            Assert.AreEqual(Number("instruction"), Number("base") + Number("offset"));
            Assert.IsGreaterThan(Number("offset"), Number("image-size"));
        }
        ulong Number(string name) => ulong.Parse(values[name][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

using Csls.Debugger.Dump;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.DataReaders.Implementation;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies independently captured Windows stacks preserve the selected processes and their DAP connection.
/// </summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
[SupportedOSPlatform("windows")]
public sealed class DapWindowsNativeDiagnosticsTests : DapTestContext
{
    /// <summary>
    /// Captures each storage policy during native module churn and preserves the original process and memory.
    /// </summary>
    /// <param name="captureType">The independently selected Windows dump storage policy.</param>
    [TestMethod]
    [DataRow(DumpType.Normal)]
    [DataRow(DumpType.Triage)]
    [DataRow(DumpType.WithHeap)]
    [DataRow(DumpType.Full)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeCaptureSurvivesModuleChurn(DumpType captureType)
    {
        string directory = Directory.CreateTempSubdirectory("csls-windows-module-capture-").FullName;
        try
        {
            string stopPath = Path.Join(directory, "stop.signal");
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(ResolveTestProcessHost());
            start.ArgumentList.Add("--windows-module-churn");
            start.ArgumentList.Add(stopPath);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The target did not start.");
            Task<string> error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            Task<string>? output = null;
            try
            {
                string? announcement = await process.StandardOutput.ReadLineAsync(TestContext.CancellationToken)
                    .ConfigureAwait(false);
                Assert.IsNotNull(announcement);
                Assert.StartsWith("ready:", announcement);
                string[] addresses = announcement.Split(':');
                Assert.HasCount(4, addresses);
                ulong address = ulong.Parse(addresses[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ulong mapped = ulong.Parse(addresses[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                ulong reserved = ulong.Parse(addresses[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                for (int index = 0; index < 4; index++)
                {
                    string path = Path.Join(directory, $"capture-{index}.dmp");
                    long started = Stopwatch.GetTimestamp();
                    (int exitCode, string collectedOutput, string collectedError) =
                        await WindowsDebuggerProcessCapture.CaptureAsync(process, path, TestContext.CancellationToken,
                            captureType).ConfigureAwait(false);
                    TestContext.WriteLine($"{captureType} capture {index}: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
                    Assert.AreEqual(0, exitCode, collectedOutput + collectedError);
                    Assert.IsFalse(process.HasExited);
                    using var dump = DataTarget.LoadDump(path, new DataTargetOptions { SymbolPaths = [] });
                    Assert.AreEqual(process.Id,
                        DumpProcessIdentity.Read(dump.DataReader, path, TestContext.CancellationToken));
                    _ = Assert.ContainsSingle(dump.ClrVersions);
                    IThreadReader threads = Assert.IsInstanceOfType<IThreadReader>(dump.DataReader);
                    Assert.IsNotEmpty(threads.EnumerateOSThreadIds());
                    if (captureType is DumpType.WithHeap or DumpType.Full)
                    {
                        byte[] memory = new byte[4096];
                        foreach (ulong offset in new ulong[] { 0, 512 * 1024, 1024 * 1024 - 4096 })
                        {
                            Assert.AreEqual(memory.Length, dump.DataReader.Read(checked(address + offset), memory));
                            Assert.AreEqual(-1, memory.AsSpan().IndexOfAnyExcept((byte)0x5a));
                            if (captureType == DumpType.Full)
                            {
                                Assert.AreEqual(memory.Length, dump.DataReader.Read(checked(mapped + offset), memory),
                                    "Full capture must retain readable anonymous mapped pages.");
                                Assert.AreEqual(-1, memory.AsSpan().IndexOfAnyExcept((byte)0xa6));
                                Assert.AreEqual(memory.Length, dump.DataReader.Read(checked(reserved + offset), memory),
                                    "Full capture must retain committed pages from a reserved executable mapping.");
                                Assert.AreEqual(-1, memory.AsSpan().IndexOfAnyExcept((byte)0xc3));
                            }
                        }
                    }
                    if (captureType == DumpType.Full)
                    {
                        AssertFullImageMemory(path, dump.DataReader);
                    }
                    Assert.IsFalse(File.Exists(path + ".mapped"), "The collector must release its mapped-memory storage.");
                }
                await File.WriteAllTextAsync(stopPath, "stop", TestContext.CancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
                Assert.AreEqual(0, process.ExitCode, await error.ConfigureAwait(false));
                string completion = (await output.ConfigureAwait(false)).Trim();
                Assert.StartsWith("completed:", completion);
                Assert.IsGreaterThan(1L, long.Parse(completion.AsSpan("completed:".Length), CultureInfo.InvariantCulture));
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                _ = await error.ConfigureAwait(false);
                if (output is not null)
                {
                    _ = await output.ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private static void AssertFullImageMemory(string path, IDataReader dataReader)
    {
        using FileStream file = File.OpenRead(path);
        using var reader = new BinaryReader(file);
        file.Position = 8;
        uint streamCount = reader.ReadUInt32();
        uint directory = reader.ReadUInt32();
        Assert.IsLessThanOrEqualTo(128u, streamCount);
        for (uint stream = 0; stream < streamCount; stream++)
        {
            file.Position = checked(directory + stream * 12L);
            uint type = reader.ReadUInt32();
            _ = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            if (type != 16) // MemoryInfoListStream records the writer's complete virtual address map.
            {
                continue;
            }
            file.Position = offset;
            uint headerSize = reader.ReadUInt32();
            uint entrySize = reader.ReadUInt32();
            ulong entries = reader.ReadUInt64();
            Assert.AreEqual(16u, headerSize);
            Assert.AreEqual(48u, entrySize);
            Assert.IsGreaterThan(0UL, entries);
            Assert.IsLessThanOrEqualTo(100000UL, entries);
            int images = 0;
            byte[] memory = new byte[4096];
            for (ulong index = 0; index < entries; index++)
            {
                file.Position = checked(offset + headerSize + (long)index * entrySize);
                ulong address = reader.ReadUInt64();
                file.Position += 16;
                ulong size = reader.ReadUInt64();
                uint state = reader.ReadUInt32();
                uint protection = reader.ReadUInt32();
                uint memoryType = reader.ReadUInt32();
                if (state != 0x1000 || memoryType != 0x1000000 || (protection & 0x101) != 0 || (protection & 0xee) == 0)
                {
                    continue;
                }
                Assert.IsGreaterThan(0UL, size);
                int length = checked((int)Math.Min((ulong)memory.Length, size));
                Assert.AreEqual(length, dataReader.Read(address, memory.AsSpan(0, length)),
                    $"Full capture advertises image storage absent from the snapshot at 0x{address:x}.");
                Assert.AreEqual(length, dataReader.Read(checked(address + size - (uint)length), memory.AsSpan(0, length)));
                images++;
            }
            Assert.IsGreaterThan(0, images);
            return;
        }
        Assert.Fail("Full capture must record its virtual address map.");
    }

    /// <summary>
    /// Captures the stopped target and adapter with their exact identities and retains a usable debugging session.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeTreeCapturePreservesStoppedSession()
    {
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        (int threadId, int processId) = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--print-environment", "CSLS_DEBUGGER_ENTRY_VALUE"]).ConfigureAwait(false);
        using var targetProcess = Process.GetProcessById(processId);
        _ = targetProcess.SafeHandle;
        string directory = await WindowsDebuggerProcessCapture.CaptureTreeAsync(client.HostProcessId, TestContext)
            .ConfigureAwait(false);
        try
        {
            foreach (int id in new[] { client.HostProcessId, processId })
            {
                string dumpPath = Path.Join(directory, $"process-{id}.dmp");
                using var dump = DataTarget.LoadDump(dumpPath, new DataTargetOptions { SymbolPaths = [] });
                Assert.AreEqual(id, DumpProcessIdentity.Read(dump.DataReader, dumpPath, TestContext.CancellationToken));
                IThreadReader threads = Assert.IsInstanceOfType<IThreadReader>(dump.DataReader);
                Assert.IsNotEmpty(threads.EnumerateOSThreadIds());
                if (id == processId)
                {
                    Assert.Contains(checked((uint)threadId), threads.EnumerateOSThreadIds());
                }
            }
            Assert.IsFalse(targetProcess.HasExited);
            int request = await client.SendRequestAsync("threads", WriteEmptyObject, TestContext.CancellationToken)
                .ConfigureAwait(false);
            using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            AssertResponse(response.RootElement, request, "threads", success: true);
            Assert.Contains(threadId, response.RootElement.GetProperty("body").GetProperty("threads").EnumerateArray()
                .Select(thread => thread.GetProperty("id").GetInt32()));
            await ContinueEntryToExitAsync(client, threadId, "entry-result").ConfigureAwait(false);
            await targetProcess.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, targetProcess.ExitCode);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Preserves a completed dump when a later collector is given its existing path.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeCapturePreservesExistingFileAndTarget()
    {
        string directory = Directory.CreateTempSubdirectory("csls-windows-capture-").FullName;
        try
        {
            DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
            int initialize = await client.SendInitializeRequestAsync(TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, initialize, "initialize", success: true);
            }
            using var process = Process.GetProcessById(client.HostProcessId);
            _ = process.SafeHandle;
            string path = Path.Join(directory, "adapter.dmp");
            (int exitCode, string output, string error) = await WindowsDebuggerProcessCapture.CaptureAsync(
                process, path, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(0, exitCode, output + error);
            byte[] original = await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false);
            (exitCode, output, error) = await WindowsDebuggerProcessCapture.CaptureAsync(
                process, path, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(0, exitCode, output + error);
            Assert.Contains("IOException", error);
            Assert.AreSequenceEqual(original,
                await File.ReadAllBytesAsync(path, TestContext.CancellationToken).ConfigureAwait(false));
            Assert.IsFalse(process.HasExited);
            await DisconnectAsync(client).ConfigureAwait(false);
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects a stale creation-time identity before creating an output file or changing the live process.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task NativeCaptureRejectsChangedProcessIdentity()
    {
        string directory = Directory.CreateTempSubdirectory("csls-windows-capture-identity-").FullName;
        try
        {
            using var process = Process.GetCurrentProcess();
            string path = Path.Join(directory, "unexpected.dmp");
            var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet");
            startInfo.ArgumentList.Add(ResolveTestProcessHost());
            startInfo.ArgumentList.Add("--windows-native-dump");
            startInfo.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add((process.StartTime.ToUniversalTime().ToFileTimeUtc() + 1)
                .ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(path);
            (int exitCode, string output, string error) = await DebuggerTestProcess.RunAsync(
                startInfo, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreNotEqual(0, exitCode, output + error);
            Assert.Contains("The selected process identity has changed.", error);
            Assert.IsFalse(File.Exists(path));
        }
        finally
        {
            await DebuggerTestDirectoryReleaseWaiter.DeleteAsync(directory, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}

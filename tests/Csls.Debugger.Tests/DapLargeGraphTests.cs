using Csls.EndToEndPerformance;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Csls.Debugger.Tests;

/// <summary>
/// Inspects independently measured four-GiB targets through bounded DAP pages and observes owned-process cleanup.
/// </summary>
[TestClass]
public sealed partial class DapLargeGraphTests : DapTestContext
{
    private const long TargetBytes = 4L * 1024 * 1024 * 1024;
    private const int ChunkLength = 67108864;
    private const int ResponseBudget = 128 * 1024;
    private const long AdapterGrowthBudget = 128L * 1024 * 1024;

    /// <summary>
    /// Preserves bounded pages and cyclic paths while rejecting oversized expansions of distinct four-GiB target storage.
    /// </summary>
    [TestMethod]
    [TestCategory("DebuggerStress")]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task FourGiBGraphPreservesBoundedInspectionAndShutdown()
    {
        Assert.IsTrue(Environment.Is64BitProcess, "The four-GiB debugger stress fixture requires a 64-bit runner.");
        Assert.IsGreaterThanOrEqualTo(8L * 1024 * 1024 * 1024,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            "The four-GiB debugger stress fixture requires at least eight GiB of runner memory capacity.");
        DapTestClient client = await DapTestClient.CreateAsync(TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable cleanup = client.ConfigureAwait(false);
        using DapTestCancellationCapture capture = CaptureProtocolOnCancellation(client);
        (int thread, int processId) = await LaunchAtEntryAsync(client, ResolveTestProcessHost(),
            ["--debugger-large-graph-fixture"]).ConfigureAwait(false);
        using var target = Process.GetProcessById(processId);
        try
        {
            string source = Path.Join(FindRepositoryRoot(), "tests", "Csls.TestProcessHost", "DebuggerLargeGraphFixture.cs");
            int line = FindSourceLine(await File.ReadAllLinesAsync(source, TestContext.CancellationToken).ConfigureAwait(false),
                "DebuggerBlockingWait.Wait(\"large-graph-ready\");");
            int breakpoint = await client.SendRequestAsync("setBreakpoints",
                writer => WriteSourceBreakpointArguments(writer, source, line), TestContext.CancellationToken).ConfigureAwait(false);
            using (JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false))
            {
                AssertResponse(response.RootElement, breakpoint, "setBreakpoints", success: true);
            }
            thread = await ContinueToBreakpointAsync(client, thread).ConfigureAwait(false);
            JsonElement frame = Assert.ContainsSingle((await ReadDeepStackPageAsync(client, thread, 0, 1)
                .ConfigureAwait(false)).GetProperty("stackFrames").EnumerateArray());
            Assert.AreEqual(line, frame.GetProperty("line").GetInt32());
            Assert.AreEqual("Csls.TestProcessHost.DebuggerLargeGraphFixture.Run", frame.GetProperty("name").GetString());
            int frameId = frame.GetProperty("id").GetInt32();
            Assert.IsGreaterThanOrEqualTo(TargetBytes, ReadTargetStorage(target),
                "The operating system must observe the populated target storage independently of its logical array counts.");

            JsonElement chunks = await ReadEvaluationAsync(client, frameId, "chunks", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(64, chunks.GetProperty("indexedVariables").GetInt32());
            JsonElement[] arrays = await ReadPageAsync(client, chunks.GetProperty("variablesReference").GetInt32(), 0, 64)
                .ConfigureAwait(false);
            Assert.HasCount(64, arrays);
            var references = new List<int>();
            long observedLength = 0;
            for (int index = 0; index < arrays.Length; index++)
            {
                Assert.AreEqual($"[{index}]", arrays[index].GetProperty("name").GetString());
                Assert.AreEqual(ChunkLength, arrays[index].GetProperty("indexedVariables").GetInt32());
                observedLength += arrays[index].GetProperty("indexedVariables").GetInt32();
                int reference = arrays[index].GetProperty("variablesReference").GetInt32();
                references.Add(reference);
                await AssertPageAsync(client, reference, 0, 4, index + 1).ConfigureAwait(false);
                await AssertPageAsync(client, reference, ChunkLength - 4, 4, index + 1).ConfigureAwait(false);
            }
            Assert.AreEqual(TargetBytes, observedLength);
            Assert.HasCount(64, references.Distinct());

            ProcessTreeSnapshot before = await ProcessTreeReader.CaptureAsync(client.HostProcessId, TestContext.CancellationToken)
                .ConfigureAwait(false);
            long beforeMemory = ReadAdapterMemory(before, target, "before repeated pages");
            for (int page = 0; page < 256; page++)
            {
                int selected = page % references.Count;
                int start = page * 4096;
                await AssertPageAsync(client, references[selected], start, 256, selected + 1).ConfigureAwait(false);
            }
            JsonElement cycle = await ReadEvaluationAsync(client, frameId, "cycle", success: true,
                TestContext.CancellationToken).ConfigureAwait(false);
            int cyclicReference = cycle.GetProperty("variablesReference").GetInt32();
            for (int depth = 0; depth < 128; depth++)
            {
                JsonElement value = Assert.ContainsSingle(await ReadPageAsync(client, cyclicReference, 0, 1).ConfigureAwait(false));
                Assert.AreEqual("[0]", value.GetProperty("name").GetString());
                Assert.AreEqual(1, value.GetProperty("indexedVariables").GetInt32());
                cyclicReference = value.GetProperty("variablesReference").GetInt32();
                Assert.IsGreaterThan(0, cyclicReference);
            }
            foreach (int count in new[] { 0, 65537, ChunkLength })
            {
                JsonElement rejected = await RequestPageAsync(client, references[0], 0, count, success: false)
                    .ConfigureAwait(false);
                Assert.Contains("limit of 65536 elements", rejected.GetProperty("message").GetString()!);
                await AssertPageAsync(client, references[0], ChunkLength - 1, 1, 1).ConfigureAwait(false);
            }
            Assert.IsEmpty(await ReadPageAsync(client, references[^1], ChunkLength, 1).ConfigureAwait(false));
            JsonElement refreshed = Assert.ContainsSingle((await ReadDeepStackPageAsync(client, thread, 0, 1)
                .ConfigureAwait(false)).GetProperty("stackFrames").EnumerateArray());
            Assert.AreEqual(frameId, refreshed.GetProperty("id").GetInt32());
            Assert.AreEqual(line, refreshed.GetProperty("line").GetInt32());
            Assert.AreEqual(frame.GetProperty("name").GetString(), refreshed.GetProperty("name").GetString());
            Assert.AreEqual(frame.GetProperty("column").GetInt32(), refreshed.GetProperty("column").GetInt32());
            Assert.AreEqual(frame.GetProperty("source").GetProperty("path").GetString(),
                refreshed.GetProperty("source").GetProperty("path").GetString());
            ProcessTreeSnapshot after = await ProcessTreeReader.CaptureAsync(client.HostProcessId, TestContext.CancellationToken)
                .ConfigureAwait(false);
            long afterMemory = ReadAdapterMemory(after, target, "after repeated pages");
            Assert.IsEmpty(after.ProcessIds.Except(before.ProcessIds),
                "Repeated bounded pages must not retain additional owned processes.");
            Assert.IsLessThanOrEqualTo(AdapterGrowthBudget, afterMemory - beforeMemory,
                "Repeated bounded pages must not copy the target graph into the adapter.");

            await DisconnectAndObserveAsync(client, after.ProcessIds, 0).ConfigureAwait(false);
            Assert.IsEmpty(client.Diagnostics.ToString());
        }
        catch
        {
            TestContext.WriteLine(client.ProtocolTranscript);
            TestContext.WriteLine(client.Diagnostics.ToString());
            throw;
        }
    }

    private long ReadTargetStorage(Process target)
    {
        // Windows private commitment includes pages evicted from the working set.
        // Trim the stopped target so its subsequent DAP pages also exercise that condition.
        if (OperatingSystem.IsWindows() && K32EmptyWorkingSet(target.SafeHandle) == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        target.Refresh();
        TestContext.WriteLine($"Four-GiB target {target.Id}: resident={target.WorkingSet64}, private={target.PrivateMemorySize64}.");
        return OperatingSystem.IsWindows() ? target.PrivateMemorySize64 : target.WorkingSet64;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32", SetLastError = true)]
    private static partial int K32EmptyWorkingSet(SafeProcessHandle process);

    private async Task DisconnectAndObserveAsync(DapTestClient client, IReadOnlyList<int> processIds, int index)
    {
        if (index == processIds.Count)
        {
            await DisconnectAsync(client).ConfigureAwait(false);
            return;
        }

        // Retain each observed process before disconnecting, and release every handle as this call unwinds.
        using var process = Process.GetProcessById(processIds[index]);
        await DisconnectAndObserveAsync(client, processIds, index + 1).ConfigureAwait(false);
        await process.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false);
        Assert.IsTrue(process.HasExited);
    }

    private async Task<int> ContinueToBreakpointAsync(DapTestClient client, int thread)
    {
        int sequence = await client.SendRequestAsync("continue", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("threadId", thread);
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        bool responded = false;
        bool continued = false;
        int? stoppedThread = null;
        while (!responded || !continued || stoppedThread is null)
        {
            using JsonDocument message = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
            JsonElement root = message.RootElement;
            if (root.GetProperty("type").GetString() == "response")
            {
                AssertResponse(root, sequence, "continue", success: true);
                Assert.IsFalse(responded);
                responded = true;
            }
            else if (root.GetProperty("event").GetString() == "continued")
            {
                Assert.IsFalse(continued);
                continued = true;
            }
            else
            {
                AssertEvent(root, "stopped");
                Assert.IsNull(stoppedThread);
                JsonElement body = root.GetProperty("body");
                Assert.AreEqual("breakpoint", body.GetProperty("reason").GetString());
                Assert.IsTrue(body.GetProperty("allThreadsStopped").GetBoolean());
                stoppedThread = body.GetProperty("threadId").GetInt32();
            }
        }
        return stoppedThread.Value;
    }

    private long ReadAdapterMemory(ProcessTreeSnapshot snapshot, Process target, string phase)
    {
        Assert.Contains(target.Id, snapshot.ProcessIds);
        target.Refresh();
        long memory = snapshot.WorkingSetBytes - target.WorkingSet64;
        Assert.IsGreaterThan(0L, memory);
        TestContext.WriteLine($"Four-GiB inspection {phase}: processes={string.Join(',', snapshot.ProcessIds)}, " +
            $"targetResident={target.WorkingSet64}, adapterResident={memory}.");
        return memory;
    }

    private async Task AssertPageAsync(DapTestClient client, int reference, int start, int count, int value)
    {
        JsonElement[] page = await ReadPageAsync(client, reference, start, count).ConfigureAwait(false);
        Assert.HasCount(count, page);
        for (int index = 0; index < page.Length; index++)
        {
            Assert.AreEqual($"[{start + index}]", page[index].GetProperty("name").GetString());
            Assert.AreEqual(value.ToString(CultureInfo.InvariantCulture), page[index].GetProperty("value").GetString());
            Assert.AreEqual("byte", page[index].GetProperty("type").GetString());
            Assert.AreEqual(0, page[index].GetProperty("variablesReference").GetInt32());
        }
    }

    private async Task<JsonElement[]> ReadPageAsync(DapTestClient client, int reference, int start, int count)
    {
        JsonElement response = await RequestPageAsync(client, reference, start, count, success: true).ConfigureAwait(false);
        return [.. response.GetProperty("body").GetProperty("variables").EnumerateArray()];
    }

    private async Task<JsonElement> RequestPageAsync(DapTestClient client, int reference, int start, int count, bool success)
    {
        int sequence = await client.SendRequestAsync("variables", writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("variablesReference", reference);
            writer.WriteNumber("start", start);
            writer.WriteNumber("count", count);
            writer.WriteString("filter", "indexed");
            writer.WriteEndObject();
        }, TestContext.CancellationToken).ConfigureAwait(false);
        using JsonDocument response = await client.ReadMessageAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertResponse(response.RootElement, sequence, "variables", success);
        Assert.IsLessThanOrEqualTo(ResponseBudget, Encoding.UTF8.GetByteCount(response.RootElement.GetRawText()));
        return response.RootElement.Clone();
    }
}

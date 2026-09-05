using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies bounded debugger-control output retention and cursor semantics.
/// </summary>
[TestClass]
public sealed class DebuggerControlOutputTests
{
    /// <summary>
    /// Reports evicted entries and pages the retained output deterministically.
    /// </summary>
    [TestMethod]
    public async Task OutputPagesReportRetentionGapAndStableCursor()
    {
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable cleanup = service.ConfigureAwait(false);
        for (int index = 1; index <= 1030; index++)
        {
            await service.OnOutputAsync(
                DebugOutputCategory.StandardOutput,
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None).ConfigureAwait(false);
        }

        DebugOutputPage first = await service.GetOutputAsync(
            new DebugOutputRequest(0, 256),
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(256, first.Entries);
        Assert.AreEqual(7L, first.FirstRetainedSequence);
        Assert.AreEqual(6L, first.DroppedBeforeStart);
        Assert.AreEqual(262L, first.NextSequence);
        Assert.IsTrue(first.HasMore);
        Assert.AreEqual("7", first.Entries[0].Output);

        DebugOutputPage last = await service.GetOutputAsync(
            new DebugOutputRequest(1024, 256),
            CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(6, last.Entries);
        Assert.AreEqual(1030L, last.NextSequence);
        Assert.IsFalse(last.HasMore);
        Assert.AreEqual("1030", last.Entries[^1].Output);
    }

    /// <summary>
    /// Bounds a single hostile output segment while preserving its newest text.
    /// </summary>
    [TestMethod]
    public async Task OutputSegmentIsBoundedAndMarkedTruncated()
    {
        var service = new DebuggerControlService();
        await using ConfiguredAsyncDisposable cleanup = service.ConfigureAwait(false);
        await service.OnOutputAsync(
            DebugOutputCategory.StandardError,
            new string('x', 9000),
            CancellationToken.None).ConfigureAwait(false);

        DebugOutputPage page = await service.GetOutputAsync(
            new DebugOutputRequest(0, 1),
            CancellationToken.None).ConfigureAwait(false);
        DebugOutputEntry entry = Assert.ContainsSingle(page.Entries);
        Assert.AreEqual(DebugOutputCategory.StandardError, entry.Category);
        Assert.HasCount(8192, entry.Output);
        Assert.IsTrue(entry.Truncated);
    }
}

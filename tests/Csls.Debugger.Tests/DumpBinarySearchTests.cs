using Csls.Debugger.Contracts;
using Csls.Debugger.Dump;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies explicit binary directory validation and activation recovery using real captured process dumps.
/// </summary>
[TestClass]
public sealed class DumpBinarySearchTests : DapTestContext
{
    /// <summary>
    /// Rejects invalid local search roots before acquiring a dump and preserves subsequent activation.
    /// </summary>
    /// <param name="input">The invalid path or directory-count partition.</param>
    [TestMethod]
    [DataRow("relative")]
    [DataRow("empty")]
    [DataRow("network")]
    [DataRow("device")]
    [DataRow("missing")]
    [DataRow("file")]
    [DataRow("nul")]
    [DataRow("long")]
    [DataRow("over-budget")]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DumpBinaryPathsRejectInvalidDirectoriesAndRecover(string input)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string directory = Path.GetDirectoryName(fixture.DumpPath)
            ?? throw new InvalidOperationException("The fixture has no directory.");
        string[] paths = input switch
        {
            "relative" => ["module"],
            "empty" => [string.Empty],
            "network" => ["//server/share"],
            "device" => [@"\\?\C:\windows"],
            "missing" => [Path.Join(directory, "absent")],
            "file" => [fixture.DumpPath],
            "nul" => [directory + '\0'],
            "long" => [directory + new string('x', 32769)],
            "over-budget" => [.. Enumerable.Repeat(directory, 65)],
            _ => throw new InvalidOperationException($"Unknown invalid partition: {input}")
        };
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        ArgumentException failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.OpenDumpAsync(
            new DebugDumpOpenRequest(fixture.DumpPath, BinarySearchPaths: paths), TestContext.CancellationToken)).ConfigureAwait(false);
        Assert.AreEqual("paths", failure.ParamName);
        Assert.AreEqual(DebugSessionState.Created, (await service.GetSessionAsync(TestContext.CancellationToken)
            .ConfigureAwait(false)).State);
        DebugSessionSnapshot opened = await service.OpenDumpAsync(fixture.OpenRequest, TestContext.CancellationToken)
            .ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, opened.State);
        Assert.AreEqual(fixture.ProcessId, opened.ProcessId);
        Assert.IsNotEmpty(await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Opens real dumps with omitted, empty, singleton, and maximum-size explicit directory lists.
    /// </summary>
    /// <param name="count">The path count, or minus one for the omitted option.</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(63)]
    [DataRow(64)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task DumpBinaryPathsAcceptDirectoryCountBoundaries(int count)
    {
        DebuggerDumpFixture fixture = await DebuggerDumpFixture.CreateAsync(ResolveTestProcessHost(),
            TestContext.CancellationToken).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable fixtureCleanup = fixture.ConfigureAwait(false);
        string directory = Path.GetDirectoryName(fixture.ProgramPath)
            ?? throw new InvalidOperationException("The fixture has no directory.");
        var service = new DumpDebuggerControlService();
        await using ConfiguredAsyncDisposable serviceCleanup = service.ConfigureAwait(false);
        DebugSessionSnapshot opened = await service.OpenDumpAsync(new DebugDumpOpenRequest(fixture.DumpPath,
            BinarySearchPaths: count < 0 ? null : Enumerable.Repeat(directory, count).ToArray()),
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(DebugSessionState.Stopped, opened.State);
        Assert.AreEqual(fixture.ProcessId, opened.ProcessId);
        Assert.IsNotEmpty(await service.GetThreadsAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(DebugSessionState.Terminated, (await service.DetachAsync(TestContext.CancellationToken)
            .ConfigureAwait(false)).State);
        using FileStream released = File.Open(fixture.DumpPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsGreaterThan(0L, released.Length);
    }
}

using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies source stepping through real compiler-generated iterators.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Presents iterator frames as their user-authored method and steps to the consumer.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task StepOverYieldReturnStopsInIteratorConsumer()
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourcePath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerIteratorStepFixture.cs");
        string[] sourceLines = await File.ReadAllLinesAsync(
            sourcePath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int yieldLine = FindSourceLine(sourceLines, "yield return value;");
        int consumerEntryLine = FindSourceLine(sourceLines, "foreach (int value");
        int consumerBlockLine = FindSourceLine(sourceLines, "        {");
        int consumerBodyLine = FindSourceLine(sourceLines, "total += value;");

        DapTestClient client = await DapTestClient
            .CreateAsync(TestContext.CancellationToken)
            .ConfigureAwait(false);
        await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
        int threadId = await LaunchToSourceBreakpointAsync(
            client,
            sourcePath,
            yieldLine,
            ["--debugger-iterator-step-fixture"]).ConfigureAwait(false);
        (string initialName, string? initialPath, int initialLine) =
            await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            "Csls.TestProcessHost.DebuggerIteratorStepFixture.EnumerateValues",
            initialName);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, initialPath));
        Assert.AreEqual(yieldLine, initialLine);

        threadId = await StepAndReadStopAsync(
            client,
            "next",
            threadId,
            TestContext.CancellationToken).ConfigureAwait(false);
        (string resumedName, string? resumedPath, int actualConsumerEntryLine) =
            await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerIteratorStepFixture.Run", resumedName);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, resumedPath));
        Assert.AreEqual(consumerEntryLine, actualConsumerEntryLine);

        threadId = await StepAndReadStopAsync(
            client,
            "next",
            threadId,
            TestContext.CancellationToken).ConfigureAwait(false);
        (resumedName, resumedPath, int actualConsumerMoveLine) =
            await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerIteratorStepFixture.Run", resumedName);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, resumedPath));
        Assert.AreEqual(consumerEntryLine, actualConsumerMoveLine);

        threadId = await StepAndReadStopAsync(
            client,
            "next",
            threadId,
            TestContext.CancellationToken).ConfigureAwait(false);
        (resumedName, resumedPath, int actualConsumerBlockLine) =
            await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerIteratorStepFixture.Run", resumedName);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, resumedPath));
        Assert.AreEqual(consumerBlockLine, actualConsumerBlockLine);

        threadId = await StepAndReadStopAsync(
            client,
            "next",
            threadId,
            TestContext.CancellationToken).ConfigureAwait(false);
        (resumedName, resumedPath, int actualConsumerBodyLine) =
            await ReadSourceFrameAsync(
                client,
                threadId,
                sourcePath,
                TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual("Csls.TestProcessHost.DebuggerIteratorStepFixture.Run", resumedName);
        Assert.IsTrue(DebuggerTestPath.AreEquivalent(sourcePath, resumedPath));
        Assert.AreEqual(consumerBodyLine, actualConsumerBodyLine);

        int continueSequence = await client.SendRequestAsync(
            "continue",
            WriteEmptyObject,
            TestContext.CancellationToken).ConfigureAwait(false);
        await ReadSuccessfulTerminationAsync(
            client,
            continueSequence,
            TestContext.CancellationToken).ConfigureAwait(false);
        Assert.AreEqual(
            0,
            await client.WaitForExitAsync(TestContext.CancellationToken).ConfigureAwait(false));
        Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
    }
}

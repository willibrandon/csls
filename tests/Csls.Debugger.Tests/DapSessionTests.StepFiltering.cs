using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies managed property step filtering through a real CoreCLR process.
/// </summary>
public sealed partial class DapSessionTests
{
    /// <summary>
    /// Skips property accessors by default and enters them when filtering is disabled.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task EnableStepFilteringControlsPropertyStepIn()
    {
        string repositoryRoot = FindRepositoryRoot();
        string callerPath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerStepFilteringFixture.cs");
        string propertyPath = Path.Join(
            repositoryRoot,
            "tests",
            "Csls.TestProcessHost",
            "DebuggerStepFilteringValue.cs");
        string[] callerLines = await File.ReadAllLinesAsync(
            callerPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        string[] propertyLines = await File.ReadAllLinesAsync(
            propertyPath,
            TestContext.CancellationToken).ConfigureAwait(false);
        int callLine = FindSourceLine(callerLines, "int answer = value.Answer;");
        int nextLine = FindSourceLine(callerLines, "while (!File.Exists(path))");
        int getterLine = FindSourceLine(propertyLines, "        get") + 1;
        await AssertPropertyStepAsync(
            callerPath,
            callLine,
            expectedPath: callerPath,
            expectedLine: nextLine,
            enableStepFiltering: true).ConfigureAwait(false);
        await AssertPropertyStepAsync(
            callerPath,
            callLine,
            expectedPath: propertyPath,
            expectedLine: getterLine,
            enableStepFiltering: false).ConfigureAwait(false);
    }

    private async Task AssertPropertyStepAsync(
        string callerPath,
        int callLine,
        string expectedPath,
        int expectedLine,
        bool enableStepFiltering)
    {
        string waitPath = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-step-filter-{Guid.NewGuid():N}.signal");
        try
        {
            DapTestClient client = await DapTestClient
                .CreateAsync(TestContext.CancellationToken)
                .ConfigureAwait(false);
            await using ConfiguredAsyncDisposable disposal = client.ConfigureAwait(false);
            int threadId = await StartStepFilteringTargetAsync(
                client,
                callerPath,
                callLine,
                waitPath,
                enableStepFiltering).WaitAsync(
                    TimeSpan.FromSeconds(15),
                    TestContext.CancellationToken).ConfigureAwait(false);
            threadId = await StepAndReadStopAsync(
                client,
                "stepIn",
                threadId,
                TestContext.CancellationToken).WaitAsync(
                    TimeSpan.FromSeconds(15),
                    TestContext.CancellationToken).ConfigureAwait(false);
            (string _, string? framePath, int frameLine) = await ReadSourceFrameAsync(
                client,
                threadId,
                expectedPath,
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(expectedPath, framePath);
            Assert.AreEqual(expectedLine, frameLine);
            await CompleteStepFilteringTargetAsync(client, waitPath).WaitAsync(
                TimeSpan.FromSeconds(15),
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(string.Empty, client.Diagnostics.ToString());
        }
        finally
        {
            File.Delete(waitPath);
        }
    }
}

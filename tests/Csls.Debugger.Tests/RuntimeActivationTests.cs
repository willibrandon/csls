using Csls.Debugger.Contracts;
using Csls.Debugger.Control;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Csls.Debugger.Tests;

/// <summary>
/// Verifies native CoreCLR debugger activation through real dbgshim and process boundaries.
/// </summary>
[TestClass]
public sealed class RuntimeActivationTests
{
    private DebuggerWorkerTestSession? _worker;

    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Launches a real managed target suspended and initializes its ICorDebug interface.
    /// </summary>
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task ManagedTargetActivatesThroughDbgShimAndCleansUpProcess()
    {
        string repositoryRoot = FindRepositoryRoot();
        string program = Path.Join(
            repositoryRoot,
            "artifacts",
            "bin",
            "Csls.TestProcessHost",
            "debug",
            "csls-test-process-host.dll");
        string absentSignal = Path.Join(
            Path.GetTempPath(),
            $"csls-debugger-activation-{Guid.NewGuid():N}.signal");
        _worker = await DebuggerWorkerTestSession
            .StartAsync(TestContext.CancellationToken).ConfigureAwait(false);
        DebuggerRpcClient client = _worker.Client;
        string stage = "launch";
        try
        {
            DebugSessionSnapshot running = await client.LaunchAsync(
                new DebugLaunchRequest
                {
                    Program = program,
                    WorkingDirectory = repositoryRoot,
                    Arguments = ["--wait-for-file", absentSignal],
                    SourceFileMap = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["/_/"] = repositoryRoot
                    }
                },
                TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DebugSessionState.Running, running.State);
            Assert.IsNotNull(running.ProcessId);
            int processId = running.ProcessId
                ?? throw new InvalidOperationException("The running target has no process identifier.");
            stage = $"terminate target {processId}";
            DebugSessionSnapshot terminated = await client
                .TerminateAsync(TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(DebugSessionState.Terminated, terminated.State);

            Assert.IsGreaterThan(0, processId);
            _ = Assert.ThrowsExactly<ArgumentException>(
                () => Process.GetProcessById(processId));
            Assert.IsFalse(File.Exists(absentSignal));
        }
        catch (Exception exception)
        {
            TestContext.WriteLine($"Runtime activation failed during {stage}: {exception}");
            await _worker.CaptureFailureAsync(TestContext).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reports worker shutdown independently so cleanup retains the original activation failure.
    /// </summary>
    /// <returns>The completion of worker retirement.</returns>
    [TestCleanup]
    public async Task CleanupAsync()
    {
        if (_worker is not null)
        {
            await _worker.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string FindRepositoryRoot([CallerFilePath] string sourcePath = "")
        => DebuggerTestEnvironment.FindRepositoryRoot(sourcePath);
}

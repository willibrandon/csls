using System.Reflection;
using System.Runtime.CompilerServices;

namespace Csls.Tests;

/// <summary>
/// Verifies real-process workload admission against fixed hosted-runner resources.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class ExternalWorkloadLeaseTests
{
    /// <summary>
    /// Gets the active MSTest context and its framework-managed cancellation token.
    /// </summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Bounds concurrent Roslyn workspace tests independently of host processor count.
    /// </summary>
    [TestMethod]
    public void TestAssemblyBoundsParallelWorkspaceWorkloads()
    {
        ParallelizeAttribute? parallelize = typeof(ExternalWorkloadLeaseTests)
            .Assembly
            .GetCustomAttribute<ParallelizeAttribute>();

        Assert.IsNotNull(parallelize);
        Assert.AreEqual(4, parallelize.Workers);
        Assert.AreEqual(ExecutionScope.MethodLevel, parallelize.Scope);
    }

    /// <summary>
    /// Keeps one compiler and language-server workload active on a sixteen-core hosted runner.
    /// </summary>
    [TestMethod]
    public void HostedRunnerResourcesAllowOneExternalWorkload()
    {
        int capacity = ExternalWorkloadLease.CalculateCapacity(
            logicalProcessorCount: 16,
            availableMemoryBytes: 16L * 1024 * 1024 * 1024);

        Assert.AreEqual(1, capacity);
    }

    /// <summary>
    /// Allows two external workloads only when both processor and memory budgets support them.
    /// </summary>
    [TestMethod]
    public void CapacityUsesTheMostConstrainedResource()
    {
        int processorConstrained = ExternalWorkloadLease.CalculateCapacity(
            logicalProcessorCount: 32,
            availableMemoryBytes: 64L * 1024 * 1024 * 1024);
        int memoryConstrained = ExternalWorkloadLease.CalculateCapacity(
            logicalProcessorCount: 64,
            availableMemoryBytes: 16L * 1024 * 1024 * 1024);

        Assert.AreEqual(2, processorConstrained);
        Assert.AreEqual(2, memoryConstrained);
    }

    /// <summary>
    /// Keeps queued real-process admission asynchronous when every workload slot is occupied.
    /// </summary>
    [TestMethod]
    public async Task QueuedLanguageServerStartDoesNotBlockTheTestHost()
    {
        string repositoryRoot = EditorToolResolver.FindRepositoryRoot();
        string workerPath = Path.Join(
            EditorToolResolver.ResolveArtifactsRoot(repositoryRoot),
            "bin",
            "Csls.Worker",
            "debug",
            "csls-worker.dll");
        Assert.IsTrue(File.Exists(workerPath), $"Worker not found at {workerPath}.");
        await HoldLeasesAndVerifyQueuedStartAsync(
            ExternalWorkloadLease.Capacity,
            workerPath,
            repositoryRoot,
            TestContext.CancellationToken).ConfigureAwait(false);
    }

    private static async Task HoldLeasesAndVerifyQueuedStartAsync(
        int remainingLeases,
        string workerPath,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        if (remainingLeases > 1)
        {
            using (await AcquireIsolatedAsync(cancellationToken).ConfigureAwait(false))
            {
                await HoldLeasesAndVerifyQueuedStartAsync(
                    remainingLeases - 1,
                    workerPath,
                    repositoryRoot,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        Task startTask;
        using (await AcquireIsolatedAsync(cancellationToken).ConfigureAwait(false))
        {
            startTask = StartAndDisposeLanguageServerAsync(workerPath, repositoryRoot);
            Assert.IsFalse(startTask.IsCompleted);
        }

        await startTask
            .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task StartAndDisposeLanguageServerAsync(
        string workerPath,
        string repositoryRoot)
    {
        LspProcessSession lsp = await LspProcessSession.StartAsync(
            "csls-queued-admission-worker",
            EditorToolResolver.ResolveDotNetHost(),
            [workerPath],
            repositoryRoot).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable lspCleanup = lsp.ConfigureAwait(false);
    }

    private static Task<ExternalWorkloadLease> AcquireIsolatedAsync(
        CancellationToken cancellationToken)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(
                async () => await ExternalWorkloadLease
                    .AcquireAsync(cancellationToken)
                    .ConfigureAwait(false),
                cancellationToken);
        }
    }
}

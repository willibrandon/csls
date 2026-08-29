namespace Csls.Tests;

/// <summary>
/// Verifies real-process workload admission against fixed hosted-runner resources.
/// </summary>
[TestClass]
public sealed class ExternalWorkloadLeaseTests
{
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
}

using Csls.Benchmarks;

namespace Csls.Tests;

/// <summary>
/// Verifies the configuration used to execute the benchmark suite.
/// </summary>
[TestClass]
public sealed class BenchmarkConfigurationTests
{
    /// <summary>
    /// Verifies hosted runners have enough time to build BenchmarkDotNet's generated project.
    /// </summary>
    [TestMethod]
    [TestCategory("BenchmarkConfiguration")]
    public void BuildTimeoutAllowsHostedRunnerBuildsToFinish()
    {
        TimeSpan buildTimeout = BenchmarkConfiguration.Create().BuildTimeout;

        Assert.AreEqual(TimeSpan.FromMinutes(5), buildTimeout);
    }
}

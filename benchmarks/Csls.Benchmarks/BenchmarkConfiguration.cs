using BenchmarkDotNet.Configs;

namespace Csls.Benchmarks;

/// <summary>
/// Creates the shared configuration for the csls benchmark suite.
/// </summary>
public static class BenchmarkConfiguration
{
    /// <summary>
    /// Creates the configuration used for every benchmark invocation.
    /// </summary>
    /// <returns>The benchmark configuration.</returns>
    public static IConfig Create()
    {
        return ManualConfig
            .Create(DefaultConfig.Instance)
            .WithBuildTimeout(TimeSpan.FromMinutes(5));
    }
}

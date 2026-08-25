namespace Csls.EndToEndPerformance;

/// <summary>
/// Records the machine and toolchain used for one performance report.
/// </summary>
internal sealed class PerformanceEnvironment
{
    /// <summary>
    /// Gets the operating-system description.
    /// </summary>
    public required string OperatingSystem { get; init; }

    /// <summary>
    /// Gets the .NET runtime description.
    /// </summary>
    public required string Runtime { get; init; }

    /// <summary>
    /// Gets the measured runtime identifier.
    /// </summary>
    public required string RuntimeIdentifier { get; init; }

    /// <summary>
    /// Gets the operating-system architecture.
    /// </summary>
    public required string OperatingSystemArchitecture { get; init; }

    /// <summary>
    /// Gets the measurement-process architecture.
    /// </summary>
    public required string ProcessArchitecture { get; init; }

    /// <summary>
    /// Gets the logical processor count available to the harness.
    /// </summary>
    public int ProcessorCount { get; init; }

    /// <summary>
    /// Gets the available memory limit reported by the runtime.
    /// </summary>
    public long AvailableMemoryBytes { get; init; }

    /// <summary>
    /// Gets the processor model when the operating system exposes it.
    /// </summary>
    public required string ProcessorModel { get; init; }

    /// <summary>
    /// Gets the .NET SDK version used to run the harness.
    /// </summary>
    public required string DotNetSdkVersion { get; init; }
}

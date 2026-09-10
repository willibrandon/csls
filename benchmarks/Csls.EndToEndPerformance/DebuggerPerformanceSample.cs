namespace Csls.EndToEndPerformance;

/// <summary>
/// Records one completed operation and its position in the session's request warmup.
/// </summary>
/// <param name="Name">The stable workload operation name.</param>
/// <param name="Phase">Whether this is a session operation, first request, or repeated request.</param>
/// <param name="Milliseconds">The monotonic elapsed duration.</param>
internal sealed record DebuggerPerformanceSample(string Name, string Phase, double Milliseconds);

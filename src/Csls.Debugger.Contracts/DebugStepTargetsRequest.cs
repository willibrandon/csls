namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects one generation-bound active frame for source-aware Step Into discovery.
/// </summary>
/// <param name="FrameId">The selected managed frame identifier.</param>
public sealed record DebugStepTargetsRequest(int FrameId);

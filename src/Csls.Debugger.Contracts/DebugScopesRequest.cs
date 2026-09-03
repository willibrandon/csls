namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects the scopes owned by one current-generation frame.
/// </summary>
/// <param name="FrameId">The generation-bound frame handle.</param>
public sealed record DebugScopesRequest(int FrameId);

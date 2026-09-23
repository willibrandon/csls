namespace Csls.Debugger.Contracts;

/// <summary>
/// Selects the scopes owned by one current-generation frame.
/// </summary>
/// <param name="FrameId">The logical frame identifier for the application's visible stop.</param>
public sealed record DebugScopesRequest(int FrameId);

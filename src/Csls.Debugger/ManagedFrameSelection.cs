namespace Csls.Debugger;

/// <summary>
/// Identifies a managed frame across one debugger-owned target execution.
/// </summary>
/// <param name="FrameId">The logical identifier backed by an exact retained physical activation identity.</param>
internal sealed record ManagedFrameSelection(int FrameId);

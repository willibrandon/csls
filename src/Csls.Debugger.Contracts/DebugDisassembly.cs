namespace Csls.Debugger.Contracts;

/// <summary>
/// Contains an exact-count managed-IL disassembly response.
/// </summary>
/// <param name="Instructions">The ordered instructions and required placeholders.</param>
public sealed record DebugDisassembly(IReadOnlyList<DebugInstructionInfo> Instructions);

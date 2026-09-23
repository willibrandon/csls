namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one runtime-approved destination for moving the current instruction pointer.
/// </summary>
/// <param name="Id">The generation-bound session-local target identifier.</param>
/// <param name="Label">The language-neutral destination display.</param>
/// <param name="Line">The one-based source line.</param>
/// <param name="Column">The one-based source column.</param>
/// <param name="EndLine">The one-based inclusive source end line.</param>
/// <param name="EndColumn">The one-based exclusive source end column.</param>
/// <param name="InstructionReference">The virtual managed-IL address.</param>
public sealed record DebugGotoTargetInfo(
    int Id,
    string Label,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string InstructionReference);

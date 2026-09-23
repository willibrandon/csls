namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one generation-bound call that can be selected for Step Into.
/// </summary>
/// <param name="Id">The session-local target identifier.</param>
/// <param name="Label">The language-neutral called-member display.</param>
/// <param name="Line">The one-based source line when available.</param>
/// <param name="Column">The one-based source column when available.</param>
/// <param name="EndLine">The one-based exclusive source end line when available.</param>
/// <param name="EndColumn">The one-based exclusive source end column when available.</param>
public sealed record DebugStepTargetInfo(
    int Id,
    string Label,
    int? Line,
    int? Column,
    int? EndLine,
    int? EndColumn);

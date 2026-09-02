namespace Csls.Debugger.Contracts;

/// <summary>
/// Describes one managed stack frame at a specific stop generation.
/// </summary>
/// <param name="Id">The generation-bound frame handle.</param>
/// <param name="Name">The language-neutral method display name.</param>
/// <param name="SourcePath">The source document path when symbols resolve it.</param>
/// <param name="Line">The one-based source line, or zero when unavailable.</param>
/// <param name="Column">The one-based source column, or zero when unavailable.</param>
public sealed record DebugStackFrameInfo(
    int Id,
    string Name,
    string? SourcePath,
    int Line,
    int Column);

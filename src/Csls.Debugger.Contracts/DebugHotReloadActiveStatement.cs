namespace Csls.Debugger.Contracts;

/// <summary>
/// Maps one active old method instruction to its updated source span.
/// </summary>
/// <param name="MethodToken">The method-definition metadata token.</param>
/// <param name="MethodVersion">The positive old Edit and Continue method version.</param>
/// <param name="OldIlOffset">The old active-statement managed IL offset.</param>
/// <param name="StartLine">The zero-based updated source start line.</param>
/// <param name="StartColumn">The zero-based updated source start column, or negative one.</param>
/// <param name="EndLine">The zero-based updated source end line.</param>
/// <param name="EndColumn">The zero-based updated source end column, or negative one.</param>
public sealed record DebugHotReloadActiveStatement(
    uint MethodToken,
    int MethodVersion,
    uint OldIlOffset,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

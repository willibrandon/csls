namespace Csls.Mcp.Worker;

/// <summary>
/// Maps one active old method instruction to its compiler-updated source span.
/// </summary>
/// <param name="MethodToken">The positive method-definition metadata token.</param>
/// <param name="MethodVersion">The positive old Edit and Continue method version.</param>
/// <param name="OldIlOffset">The non-negative old managed IL offset.</param>
/// <param name="StartLine">The zero-based updated source start line.</param>
/// <param name="StartColumn">The zero-based updated source start column, or negative one.</param>
/// <param name="EndLine">The zero-based updated source end line.</param>
/// <param name="EndColumn">The zero-based updated source end column, or negative one.</param>
internal sealed record McpDebugHotReloadActiveStatement(
    int MethodToken,
    int MethodVersion,
    int OldIlOffset,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);

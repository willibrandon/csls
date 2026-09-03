namespace Csls.Debugger;

/// <summary>
/// Represents one visible managed-symbol sequence point in a managed method.
/// </summary>
/// <param name="MethodToken">The containing method-definition metadata token.</param>
/// <param name="IlOffset">The zero-based method-body IL offset.</param>
/// <param name="SourcePath">The symbol document path.</param>
/// <param name="StartLine">The one-based start line.</param>
/// <param name="StartColumn">The one-based start column.</param>
/// <param name="EndLine">The one-based inclusive end line.</param>
/// <param name="EndColumn">The one-based exclusive end column.</param>
/// <param name="LanguageId">The source-language identifier from the symbol document.</param>
internal sealed record ManagedSequencePoint(
    uint MethodToken,
    int IlOffset,
    string SourcePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    Guid LanguageId);

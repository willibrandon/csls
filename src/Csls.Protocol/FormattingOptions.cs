namespace Csls.Protocol;

/// <summary>
/// Carries editor indentation and final-line preferences for document formatting.
/// </summary>
public sealed record FormattingOptions
{
    /// <summary>
    /// Gets the visual width of one indentation tab.
    /// </summary>
    public required int TabSize { get; init; }

    /// <summary>
    /// Gets whether indentation uses spaces instead of tabs.
    /// </summary>
    public required bool InsertSpaces { get; init; }

    /// <summary>
    /// Gets whether trailing whitespace should be removed.
    /// </summary>
    public bool? TrimTrailingWhitespace { get; init; }

    /// <summary>
    /// Gets whether the formatted document should end with a line terminator.
    /// </summary>
    public bool? InsertFinalNewline { get; init; }

    /// <summary>
    /// Gets whether extra final blank lines should be removed.
    /// </summary>
    public bool? TrimFinalNewlines { get; init; }
}

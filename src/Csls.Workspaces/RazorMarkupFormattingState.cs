namespace Csls.Workspaces;

/// <summary>
/// Tracks markup indentation state while formatting a Razor document.
/// </summary>
internal struct RazorMarkupFormattingState
{
    /// <summary>
    /// Gets or sets the current nested markup depth.
    /// </summary>
    internal int HtmlDepth { get; set; }

    /// <summary>
    /// Gets or sets whether the current markup tag continues onto another line.
    /// </summary>
    internal bool InTag { get; set; }

    /// <summary>
    /// Gets or sets the visual column used to align continued tag attributes.
    /// </summary>
    internal int ContinuationColumn { get; set; }

    /// <summary>
    /// Gets or sets the raw text element whose opening tag continues onto another line.
    /// </summary>
    internal string? PendingRawTextElement { get; set; }

    /// <summary>
    /// Gets or sets the raw text element whose contents must remain unchanged.
    /// </summary>
    internal string? RawTextElement { get; set; }
}

using System.Text.Json;

namespace Csls.Protocol;

/// <summary>
/// Identifies one document whose foldable source ranges are requested.
/// </summary>
public sealed class FoldingRangeParams
{
    /// <summary>
    /// Gets the target source document.
    /// </summary>
    public required TextDocumentIdentifier TextDocument { get; init; }

    /// <summary>
    /// Gets the optional client token for work-done progress.
    /// </summary>
    public JsonElement? WorkDoneToken { get; init; }

    /// <summary>
    /// Gets the optional client token for partial result progress.
    /// </summary>
    public JsonElement? PartialResultToken { get; init; }
}

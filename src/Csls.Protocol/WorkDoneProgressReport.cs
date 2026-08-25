namespace Csls.Protocol;

/// <summary>
/// Updates one active client-visible work-done progress sequence.
/// </summary>
public sealed record WorkDoneProgressReport : WorkDoneProgressValue
{
    /// <summary>
    /// Gets whether the client may request cancellation of the operation.
    /// </summary>
    public bool? Cancellable { get; init; }

    /// <summary>
    /// Gets the optional current operation detail.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the optional current completion percentage.
    /// </summary>
    public int? Percentage { get; init; }
}

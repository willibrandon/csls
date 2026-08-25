namespace Csls.Protocol;

/// <summary>
/// Starts one client-visible work-done progress sequence.
/// </summary>
public sealed record WorkDoneProgressBegin : WorkDoneProgressValue
{
    /// <summary>
    /// Gets the short operation title displayed by the client.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Gets whether the client may request cancellation of the operation.
    /// </summary>
    public bool? Cancellable { get; init; }

    /// <summary>
    /// Gets the optional initial operation detail.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets the optional initial completion percentage.
    /// </summary>
    public int? Percentage { get; init; }
}

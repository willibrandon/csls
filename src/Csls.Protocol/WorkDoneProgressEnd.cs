namespace Csls.Protocol;

/// <summary>
/// Completes one client-visible work-done progress sequence.
/// </summary>
public sealed record WorkDoneProgressEnd : WorkDoneProgressValue
{
    /// <summary>
    /// Gets the optional final operation detail.
    /// </summary>
    public string? Message { get; init; }
}

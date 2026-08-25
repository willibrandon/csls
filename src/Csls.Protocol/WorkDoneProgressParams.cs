namespace Csls.Protocol;

/// <summary>
/// Carries one typed work-done value through an LSP progress notification.
/// </summary>
public sealed record WorkDoneProgressParams
{
    /// <summary>
    /// Gets the server-generated progress token.
    /// </summary>
    public required string Token { get; init; }

    /// <summary>
    /// Gets the begin, report, or end value for the operation.
    /// </summary>
    public required WorkDoneProgressValue Value { get; init; }
}

namespace Csls.Protocol;

/// <summary>
/// Requests client registration of one server-generated work-done progress token.
/// </summary>
public sealed record WorkDoneProgressCreateParams
{
    /// <summary>
    /// Gets the server-generated progress token.
    /// </summary>
    public required string Token { get; init; }
}

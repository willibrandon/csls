namespace Csls.Protocol;

/// <summary>
/// Describes capabilities dynamically registered with an LSP client.
/// </summary>
public sealed record RegistrationParams
{
    /// <summary>
    /// Gets the ordered capability registrations.
    /// </summary>
    public required IReadOnlyList<Registration> Registrations { get; init; }
}

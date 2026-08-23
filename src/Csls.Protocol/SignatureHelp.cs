namespace Csls.Protocol;

/// <summary>
/// Carries overload labels and the client's current callable argument state.
/// </summary>
public sealed record SignatureHelp
{
    /// <summary>
    /// Gets the available callable overloads.
    /// </summary>
    public required IReadOnlyList<SignatureInformation> Signatures { get; init; }

    /// <summary>
    /// Gets the selected overload index.
    /// </summary>
    public int? ActiveSignature { get; init; }

    /// <summary>
    /// Gets the selected parameter index.
    /// </summary>
    public int? ActiveParameter { get; init; }
}

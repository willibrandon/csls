namespace Csls.Protocol;

/// <summary>
/// Advertises the workspace file-operation notifications consumed by the server.
/// </summary>
public sealed record FileOperationOptions
{
    /// <summary>
    /// Gets the registration for completed create operations.
    /// </summary>
    public FileOperationRegistrationOptions? DidCreate { get; init; }

    /// <summary>
    /// Gets the registration for completed rename operations.
    /// </summary>
    public FileOperationRegistrationOptions? DidRename { get; init; }

    /// <summary>
    /// Gets the registration for completed delete operations.
    /// </summary>
    public FileOperationRegistrationOptions? DidDelete { get; init; }
}
